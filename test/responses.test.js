import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ResponsesStreamBuilder,
  ResponsesSessionStore,
  compactResponsesHistory,
  openAIResponseToResponses,
  responsesKnownAgentIds,
  responsesMalformedToolResultCount,
  recoverMalformedResponsesToolLoop,
  recoverResponsesReadOnlyToolLoop,
  responsesReadOnlyToolLoopState,
  responsesToolMetadata,
  responsesToOpenAIRequest,
} from '../src/responses.js';

test('responsesToOpenAIRequest maps instructions, function tools, calls, and outputs', () => {
  const result = responsesToOpenAIRequest({
    model: 'client-model',
    instructions: 'Work carefully.',
    stream: true,
    max_output_tokens: 1200,
    input: [
      { role: 'user', content: [{ type: 'input_text', text: 'Inspect files' }] },
      {
        type: 'function_call',
        call_id: 'call_1',
        name: 'TaskList',
        arguments: '{}',
      },
      { type: 'function_call_output', call_id: 'call_1', output: 'No tasks' },
    ],
    tools: [{
      type: 'function',
      name: 'TaskList',
      description: 'List tasks',
      parameters: { type: 'object' },
    }],
    tool_choice: { type: 'function', name: 'TaskList' },
  }, { model: 'glm-5.2' });

  assert.equal(result.model, 'glm-5.2');
  assert.equal(result.max_tokens, 1200);
  assert.deepEqual(result.messages, [
    { role: 'system', content: 'Work carefully.' },
    { role: 'user', content: 'Inspect files' },
    {
      role: 'assistant',
      content: null,
      tool_calls: [{
        id: 'call_1',
        type: 'function',
        function: { name: 'TaskList', arguments: '{}' },
      }],
    },
    { role: 'tool', tool_call_id: 'call_1', content: 'No tasks' },
  ]);
  assert.deepEqual(result.tools[0], {
    type: 'function',
    function: {
      name: 'TaskList',
      description: 'List tasks',
      parameters: { type: 'object' },
    },
  });
  assert.deepEqual(result.tool_choice, {
    type: 'function',
    function: { name: 'TaskList' },
  });
});

test('responsesToOpenAIRequest maps Codex custom tools to string-input functions', () => {
  const result = responsesToOpenAIRequest({
    model: 'codex-model',
    input: [{
      type: 'custom_tool_call_output',
      call_id: 'call_patch',
      output: 'Done',
    }],
    tools: [{ type: 'custom', name: 'apply_patch', description: 'Apply a patch' }],
  }, { model: 'glm-5.2' });

  assert.equal(result.messages[0].role, 'tool');
  assert.equal(result.messages[0].tool_call_id, 'call_patch');
  assert.deepEqual(result.tools[0].function.parameters, {
    type: 'object',
    properties: { input: { type: 'string' } },
    required: ['input'],
    additionalProperties: false,
  });
});

test('responsesToOpenAIRequest flattens Codex namespace tools and restores calls', () => {
  const request = {
    model: 'gpt-5.5',
    input: 'Inspect the runtime',
    tools: [
      {
        type: 'namespace',
        name: 'mcp__node_repl',
        description: 'Node REPL tools',
        tools: [{
          type: 'function',
          name: 'js',
          description: 'Evaluate JavaScript',
          strict: false,
          parameters: {
            type: 'object',
            properties: { code: { type: 'string' } },
            required: ['code'],
          },
        }],
      },
      { type: 'web_search', external_web_access: true },
    ],
    tool_choice: { type: 'function', namespace: 'mcp__node_repl', name: 'js' },
  };
  const metadata = responsesToolMetadata(request);
  const converted = responsesToOpenAIRequest(request, {
    model: 'glm-5.2',
    toolMetadata: metadata,
  });

  assert.equal(converted.tools[0].function.name, 'mcp__node_repl__js');
  assert.equal(converted.tools.length, 1);
  assert.match(converted.tools[0].function.description, /Namespace: mcp__node_repl/);
  assert.deepEqual(converted.tool_choice, {
    type: 'function',
    function: { name: 'mcp__node_repl__js' },
  });

  const response = openAIResponseToResponses({
    choices: [{
      message: {
        tool_calls: [{
          id: 'call_js',
          type: 'function',
          function: { name: 'mcp__node_repl__js', arguments: '{"code":"1+1"}' },
        }],
      },
    }],
  }, {
    responseId: 'resp_namespace',
    namespaceTools: metadata.namespaceTools,
  });
  assert.equal(response.output[0].type, 'function_call');
  assert.equal(response.output[0].name, 'js');
  assert.equal(response.output[0].namespace, 'mcp__node_repl');
});

test('ResponsesStreamBuilder restores namespace on streamed function calls', () => {
  const namespaceTools = new Map([[
    'codex_app__read_thread_terminal',
    { namespace: 'codex_app', name: 'read_thread_terminal', type: 'function' },
  ]]);
  const stream = new ResponsesStreamBuilder('glm-5.2', {
    responseId: 'resp_namespace_stream',
    namespaceTools,
  });
  const events = [
    ...stream.ingest({
      choices: [{ delta: { tool_calls: [{
        index: 0,
        id: 'call_terminal',
        type: 'function',
        function: { name: 'codex_app__read_thread_terminal', arguments: '{}' },
      }] }, finish_reason: 'tool_calls' }],
    }),
    ...stream.finish(),
  ];
  const done = events.find((event) => (
    event.type === 'response.output_item.done'
    && event.item.type === 'function_call'
  ));
  assert.equal(done.item.name, 'read_thread_terminal');
  assert.equal(done.item.namespace, 'codex_app');
});

test('ResponsesStreamBuilder restores a unique namespace after the model shortens its alias', () => {
  const metadata = responsesToolMetadata({
    tools: [{
      type: 'namespace',
      name: 'multi_agent_v1',
      tools: [{
        type: 'function',
        name: 'close_agent',
        parameters: { type: 'object', properties: {} },
      }],
    }],
  });
  const stream = new ResponsesStreamBuilder('glm-5.2', {
    responseId: 'resp_short_namespace',
    namespaceTools: metadata.namespaceTools,
  });
  const events = [
    ...stream.ingest({
      choices: [{ delta: { tool_calls: [{
        index: 0,
        id: 'call_close',
        type: 'function',
        function: { name: 'close_agent', arguments: '{"target":"agent-id"}' },
      }] }, finish_reason: 'tool_calls' }],
    }),
    ...stream.finish(),
  ];
  const done = events.find((event) => (
    event.type === 'response.output_item.done'
    && event.item.type === 'function_call'
  ));
  assert.equal(done.item.name, 'close_agent');
  assert.equal(done.item.namespace, 'multi_agent_v1');
});

test('ResponsesStreamBuilder does not infer namespace for an ambiguous bare tool name', () => {
  const metadata = responsesToolMetadata({
    tools: [
      { type: 'function', name: 'close_agent', parameters: { type: 'object' } },
      {
        type: 'namespace',
        name: 'multi_agent_v1',
        tools: [{ type: 'function', name: 'close_agent', parameters: { type: 'object' } }],
      },
    ],
  });
  const response = openAIResponseToResponses({
    choices: [{ message: { tool_calls: [{
      id: 'call_plain',
      type: 'function',
      function: { name: 'close_agent', arguments: '{}' },
    }] } }],
  }, { namespaceTools: metadata.namespaceTools });
  assert.equal(response.output[0].name, 'close_agent');
  assert.equal(response.output[0].namespace, undefined);
});

test('ResponsesStreamBuilder repairs a uniquely shortened close_agent target from history', () => {
  const metadata = responsesToolMetadata({
    tools: [{
      type: 'namespace',
      name: 'multi_agent_v1',
      tools: [{ type: 'function', name: 'close_agent', parameters: { type: 'object' } }],
    }],
  });
  const request = { messages: [{
    role: 'tool',
    content: '{"agent_id":"019f52b8-6bd3-7622-9874-27f3b4522464","nickname":"Euclid"}',
  }] };
  const response = openAIResponseToResponses({
    choices: [{ message: { tool_calls: [{
      id: 'call_close',
      type: 'function',
      function: {
        name: 'close_agent',
        arguments: '{"target":"019f52b8-6bd3-762-9874-27f3b4522464"}',
      },
    }] } }],
  }, {
    namespaceTools: metadata.namespaceTools,
    knownAgentIds: responsesKnownAgentIds(request),
  });
  assert.equal(response.output[0].namespace, 'multi_agent_v1');
  assert.deepEqual(JSON.parse(response.output[0].arguments), {
    target: '019f52b8-6bd3-7622-9874-27f3b4522464',
  });
});

test('ResponsesStreamBuilder emits text and function call events in order', () => {
  const stream = new ResponsesStreamBuilder('glm-5.2', {
    responseId: 'resp_test',
    customToolNames: new Set(['apply_patch']),
  });
  const events = [
    ...stream.ingest({
      id: 'chatcmpl-1',
      choices: [{ delta: { content: 'Hello' }, finish_reason: null }],
    }),
    ...stream.ingest({
      id: 'chatcmpl-1',
      choices: [{
        delta: {
          tool_calls: [{
            index: 0,
            id: 'call_patch',
            type: 'function',
            function: { name: 'apply_patch', arguments: '{"input":"patch"}' },
          }],
        },
        finish_reason: 'tool_calls',
      }],
    }),
    ...stream.finish(),
  ];

  assert.equal(events[0].type, 'response.created');
  assert.ok(events.some((event) => event.type === 'response.output_text.delta'));
  assert.ok(events.some((event) => event.type === 'response.custom_tool_call_input.delta'));
  const toolDone = events.find((event) => (
    event.type === 'response.output_item.done'
    && event.item.type === 'custom_tool_call'
  ));
  assert.equal(toolDone.item.call_id, 'call_patch');
  assert.equal(events.at(-1).type, 'response.completed');
  assert.deepEqual(
    events.map((event) => event.sequence_number),
    events.map((_, index) => index),
  );
});

test('openAIResponseToResponses preserves tool call IDs and usage', () => {
  const response = openAIResponseToResponses({
    id: 'chatcmpl-2',
    model: 'glm-5.2',
    choices: [{
      finish_reason: 'tool_calls',
      message: {
        role: 'assistant',
        content: null,
        tool_calls: [{
          id: 'call_2',
          type: 'function',
          function: { name: 'Read', arguments: '{"path":"README.md"}' },
        }],
      },
    }],
    usage: { prompt_tokens: 5, completion_tokens: 3, total_tokens: 8 },
  }, { responseId: 'resp_2' });

  assert.equal(response.id, 'resp_2');
  assert.equal(response.output[0].type, 'function_call');
  assert.equal(response.output[0].call_id, 'call_2');
  assert.deepEqual(response.usage, {
    input_tokens: 5,
    output_tokens: 3,
    total_tokens: 8,
  });
});

test('responsesToOpenAIRequest leaves previous_response_id for the session store', () => {
  const result = responsesToOpenAIRequest({
    model: 'glm-5.2',
    input: 'hello',
    previous_response_id: 'resp_previous',
  }, { model: 'glm-5.2' });

  assert.deepEqual(result.messages, [{ role: 'user', content: 'hello' }]);
  assert.equal(result.previous_response_id, undefined);
});

test('ResponsesSessionStore resumes a tool loop from previous_response_id', () => {
  const store = new ResponsesSessionStore({ maxSessions: 2, ttlMs: 60_000 });
  store.record('resp_first', {
    model: 'glm-5.2',
    messages: [{ role: 'user', content: 'Inspect files' }],
  }, {
    choices: [{
      message: {
        role: 'assistant',
        content: null,
        tool_calls: [{
          id: 'call_1',
          type: 'function',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
    }],
  }, new Set(['apply_patch']));

  const resumed = store.resume('resp_first', {
    model: 'glm-5.2',
    messages: [{ role: 'tool', tool_call_id: 'call_1', content: 'No tasks' }],
  });
  assert.equal(resumed.request.messages.length, 3);
  assert.equal(resumed.request.messages[0].content, 'Inspect files');
  assert.equal(resumed.request.messages[1].tool_calls[0].id, 'call_1');
  assert.equal(resumed.request.messages[2].role, 'tool');
  assert.deepEqual(resumed.customToolNames, new Set(['apply_patch']));
  assert.throws(() => store.resume('resp_missing', { messages: [] }), {
    message: /previous response was not found/,
  });
});

test('ResponsesSessionStore compacts long histories without splitting tool call groups', () => {
  const store = new ResponsesSessionStore({
    maxSessions: 2,
    ttlMs: 60_000,
    maxSessionChars: 10_000,
    maxTotalChars: 20_000,
    maxHistoryChars: 700,
  });
  const messages = [
    { role: 'system', content: 'Permanent instructions' },
    { role: 'user', content: 'Build the project' },
  ];
  for (let index = 0; index < 8; index += 1) {
    messages.push({
      role: 'assistant',
      content: null,
      tool_calls: [{
        id: `call_${index}`,
        type: 'function',
        function: { name: 'shell_command', arguments: `{"command":"step ${index}"}` },
      }],
    });
    messages.push({
      role: 'tool',
      tool_call_id: `call_${index}`,
      content: `result ${index} ${'x'.repeat(80)}`,
    });
  }
  assert.equal(store.record('resp_compact', { messages }, {
    choices: [{ message: { role: 'assistant', content: 'Continue' } }],
  }), true);

  const resumed = store.resume('resp_compact', { messages: [] }).request.messages;
  assert.equal(resumed[0].content, 'Permanent instructions');
  assert.equal(resumed[1].content, 'Build the project');
  assert.match(resumed[2].content, /context compaction/);
  assert.ok(JSON.stringify(resumed).length <= 700);
  for (const message of resumed.filter((item) => item.role === 'tool')) {
    assert.ok(resumed.some((item) => item.tool_calls?.some((call) => call.id === message.tool_call_id)));
  }
  assert.ok(resumed.some((item) => item.content?.startsWith?.('result 7')));
});

test('compactResponsesHistory limits a large first request before it reaches upstream', () => {
  const messages = [
    { role: 'system', content: 'Permanent instructions' },
    { role: 'user', content: 'Initial task' },
  ];
  for (let index = 0; index < 20; index += 1) {
    messages.push({ role: 'assistant', content: `old progress ${index} ${'x'.repeat(120)}` });
  }
  messages.push({
    role: 'assistant',
    content: null,
    tool_calls: [{
      id: 'call_latest',
      type: 'function',
      function: { name: 'shell_command', arguments: '{"command":"build"}' },
    }],
  });
  messages.push({ role: 'tool', tool_call_id: 'call_latest', content: 'build failed' });

  const compacted = compactResponsesHistory(messages, 700);
  assert.ok(JSON.stringify(compacted).length <= 700);
  assert.equal(compacted[0].content, 'Permanent instructions');
  assert.equal(compacted[1].content, 'Initial task');
  assert.ok(compacted.some((message) => message.tool_calls?.[0]?.id === 'call_latest'));
  assert.ok(compacted.some((message) => message.tool_call_id === 'call_latest'));
});

test('compactResponsesHistory retains an oversized latest tool round by compacting its content', () => {
  const messages = [
    { role: 'system', content: 'Permanent instructions' },
    { role: 'user', content: 'Implement the task' },
    { role: 'assistant', content: 'Old progress that should be removed' },
    { role: 'user', content: 'Old follow-up that should be removed' },
    {
      role: 'assistant',
      tool_calls: [{
        id: 'call_latest_large',
        type: 'function',
        function: { name: 'Agent', arguments: JSON.stringify({ prompt: 'x'.repeat(800) }) },
      }],
    },
    { role: 'tool', tool_call_id: 'call_latest_large', content: `head-${'y'.repeat(1200)}-tail` },
  ];

  const compacted = compactResponsesHistory(messages, 700);

  assert.ok(JSON.stringify(compacted).length <= 700);
  assert.ok(compacted.some((message) => message.tool_calls?.[0]?.id === 'call_latest_large'));
  const result = compacted.find((message) => message.tool_call_id === 'call_latest_large');
  assert.match(result.content, /history content compacted/);
  assert.match(result.content, /^head-/);
  assert.match(result.content, /-tail$/);
  assert.equal(compacted.some((message) => message.content === 'Old progress that should be removed'), false);
});

test('ResponsesSessionStore retains known agent IDs outside compacted message history', () => {
  const store = new ResponsesSessionStore({
    maxSessionChars: 10_000,
    maxTotalChars: 20_000,
    maxHistoryChars: 400,
  });
  const agentId = '019f52b8-6bd3-7622-9874-27f3b4522464';
  const messages = [
    { role: 'user', content: 'Long task' },
    {
      role: 'assistant',
      content: null,
      tool_calls: [{
        id: 'spawn',
        type: 'function',
        function: {
          name: 'multi_agent_v1__spawn_agent',
          arguments: `{"message":"${'old task '.repeat(60)}"}`,
        },
      }],
    },
    { role: 'tool', tool_call_id: 'spawn', content: `{"agent_id":"${agentId}"}` },
    { role: 'assistant', content: 'x'.repeat(600) },
  ];
  assert.equal(store.record('resp_agents', { messages }, {
    choices: [{ message: { role: 'assistant', content: 'Continue' } }],
  }), true);
  const resumed = store.resume('resp_agents', { messages: [] });
  assert.equal(resumed.request.messages.some((message) => message.content?.includes?.(agentId)), false);
  assert.deepEqual(resumed.knownAgentIds, new Set([agentId]));
});

test('responsesMalformedToolResultCount detects only the trailing invalid-call loop', () => {
  const failure = 'failed to parse function arguments: missing field `command` at line 1 column 2';
  const messages = [
    { role: 'tool', tool_call_id: 'old', content: failure },
    { role: 'assistant', content: 'Recovered' },
    { role: 'assistant', content: null, tool_calls: [{ id: 'one' }] },
    { role: 'tool', tool_call_id: 'one', content: failure },
    { role: 'assistant', content: null, tool_calls: [{ id: 'two' }] },
    { role: 'tool', tool_call_id: 'two', content: failure },
  ];
  assert.equal(responsesMalformedToolResultCount({ messages }), 2);
  messages.push({ role: 'user', content: 'Try something else' });
  assert.equal(responsesMalformedToolResultCount({ messages }), 0);
});

test('responsesMalformedToolResultCount includes malformed JSON argument errors', () => {
  const messages = [
    { role: 'assistant', content: null, tool_calls: [{ id: 'one' }] },
    {
      role: 'tool',
      tool_call_id: 'one',
      content: 'failed to parse function arguments: EOF while parsing a string at line 1 column 118',
    },
  ];
  assert.equal(responsesMalformedToolResultCount({ messages }), 1);
});

test('responsesMalformedToolResultCount treats parallel malformed calls as one failed round', () => {
  const failure = 'failed to parse function arguments: EOF while parsing a string';
  const messages = [
    { role: 'user', content: 'Inspect the files' },
    {
      role: 'assistant',
      tool_calls: [
        { id: 'call_one', function: { name: 'functions__shell_command', arguments: '{}' } },
        { id: 'call_two', function: { name: 'functions__shell_command', arguments: '{}' } },
      ],
    },
    { role: 'tool', tool_call_id: 'call_one', content: failure },
    { role: 'tool', tool_call_id: 'call_two', content: failure },
  ];

  assert.equal(responsesMalformedToolResultCount({ messages }), 1);
});

test('recoverMalformedResponsesToolLoop removes a bad suffix and allows only one immediate recovery', () => {
  const request = {
    model: 'test-model',
    messages: [
      { role: 'user', content: 'Finish the task' },
      { role: 'assistant', tool_calls: [{ id: 'call_1' }] },
      { role: 'tool', tool_call_id: 'call_1', content: 'failed to parse function arguments: EOF' },
      { role: 'assistant', tool_calls: [{ id: 'call_2' }] },
      { role: 'tool', tool_call_id: 'call_2', content: 'failed to parse function arguments: EOF' },
    ],
  };

  const recovered = recoverMalformedResponsesToolLoop(request);
  assert.equal(recovered.messages.length, 2);
  assert.equal(recovered.messages[0].content, 'Finish the task');
  assert.match(recovered.messages[1].content, /Gateway malformed tool recovery/);
  assert.equal(request.messages.length, 5);

  const failedAgain = {
    ...recovered,
    messages: [
      ...recovered.messages,
      { role: 'assistant', tool_calls: [{ id: 'call_3' }] },
      { role: 'tool', tool_call_id: 'call_3', content: 'failed to parse function arguments: EOF' },
    ],
  };
  assert.equal(recoverMalformedResponsesToolLoop(failedAgain), null);
});

test('responsesReadOnlyToolLoopState detects inspection rounds and resets after a write', () => {
  const messages = [{ role: 'user', content: 'Implement the feature' }];
  for (let index = 0; index < 10; index += 1) {
    messages.push({
      role: 'assistant',
      tool_calls: [{
        id: `read_${index}`,
        function: {
          name: 'functions__shell_command',
          arguments: JSON.stringify({ command: `Get-Content src/file-${index}.js` }),
        },
      }],
    });
    messages.push({ role: 'tool', tool_call_id: `read_${index}`, content: 'source text' });
  }
  assert.deepEqual(responsesReadOnlyToolLoopState({ messages }), {
    rounds: 10,
    recoveryActive: false,
  });

  const recovered = recoverResponsesReadOnlyToolLoop({ messages });
  recovered.messages.push({
    role: 'assistant',
    tool_calls: [{
      id: 'read_after_recovery',
      function: { name: 'functions__read_file', arguments: '{"path":"src/latest.js"}' },
    }],
  });
  recovered.messages.push({
    role: 'tool',
    tool_call_id: 'read_after_recovery',
    content: 'latest source',
  });
  assert.deepEqual(responsesReadOnlyToolLoopState(recovered), {
    rounds: 1,
    recoveryActive: true,
  });

  recovered.messages.push({
    role: 'assistant',
    tool_calls: [{
      id: 'write_progress',
      function: { name: 'functions__apply_patch', arguments: '{}' },
    }],
  });
  recovered.messages.push({ role: 'tool', tool_call_id: 'write_progress', content: 'Done!' });
  assert.deepEqual(responsesReadOnlyToolLoopState(recovered), {
    rounds: 0,
    recoveryActive: false,
  });
});

test('ResponsesSessionStore evicts histories to stay within its memory budget', () => {
  const store = new ResponsesSessionStore({
    maxSessions: 10,
    ttlMs: 60_000,
    maxSessionChars: 220,
    maxTotalChars: 300,
  });
  const response = { choices: [{ message: { role: 'assistant', content: 'B'.repeat(20) } }] };
  assert.equal(store.record('resp_one', {
    messages: [{ role: 'user', content: 'A'.repeat(80) }],
  }, response), true);
  assert.equal(store.record('resp_two', {
    messages: [{ role: 'user', content: 'C'.repeat(80) }],
  }, response), true);

  assert.throws(() => store.resume('resp_one', { messages: [] }), /previous response was not found/);
  assert.equal(store.resume('resp_two', { messages: [] }).request.messages.length, 2);
  assert.equal(store.record('resp_oversized', {
    messages: [{ role: 'user', content: 'X'.repeat(500) }],
  }, response), false);
});

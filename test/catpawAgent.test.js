import test from 'node:test';
import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import {
  CatpawAgentSessionStore,
  buildCatpawAgentRequest,
  normalizeCatpawAgentChunk,
  summarizeCatpawToolCalls,
} from '../src/catpawAgent.js';

test('buildCatpawAgentRequest wraps Claude tools in the native Catpaw MCP tool config', () => {
  const result = buildCatpawAgentRequest({
    messages: [
      { role: 'system', content: 'Work carefully.' },
      { role: 'user', content: 'List the tasks.' },
    ],
    tools: [{
      type: 'function',
      function: {
        name: 'TaskList',
        description: 'List tasks',
        parameters: { type: 'object', properties: {} },
      },
    }],
  }, {
    conversationId: 'conversation-1',
    userModelTypeCode: 2,
  });

  assert.equal(result.conversationId, 'conversation-1');
  assert.equal(result.triggerMode, 'AGENT');
  assert.equal(result.userModelTypeCode, 2);
  assert.equal(result.agentModeConfig.id, 'claude-desktop-agent');
  assert.equal(result.agentModeConfig.type, 'CUSTOM_AGENT');
  assert.equal(result.agentModeConfig.systemPrompt, 'Work carefully.');
  const internalTools = result.agentModeConfig.tools.filter((tool) => tool.toolUseName !== 'use_mcp_tool');
  assert.ok(internalTools.length > 10);
  assert.ok(internalTools.every((tool) => tool.enable === false));
  assert.deepEqual(
    internalTools.filter((tool) => ['glob_file_search', 'list_dir', 'run_terminal_cmd'].includes(tool.toolUseName)),
    [
      { toolUseName: 'glob_file_search', enable: false },
      { toolUseName: 'list_dir', enable: false },
      { toolUseName: 'run_terminal_cmd', enable: false },
    ],
  );
  assert.deepEqual(result.agentModeConfig.tools.at(-1), {
    toolUseName: 'use_mcp_tool',
    enable: true,
    mcpTools: [{
      type: 'function',
      function: {
        name: 'TaskList',
        description: 'List tasks',
        parameters: { type: 'object', properties: {} },
      },
    }],
  });
  assert.deepEqual(result.messages, [{
    content: 'List the tasks.',
    multiModalContent: [{ type: 'text', text: 'List the tasks.' }],
    role: 'user',
    triggerMode: 'AGENT',
  }]);
});

test('buildCatpawAgentRequest maps tool calls and results with their Catpaw suggestUuid', () => {
  const suggestUuidByToolCallId = new Map([['call_1', 'suggest-1']]);
  const result = buildCatpawAgentRequest({
    messages: [
      {
        role: 'assistant',
        content: '',
        tool_calls: [{
          id: 'call_1',
          type: 'function',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
      { role: 'tool', tool_call_id: 'call_1', content: 'No tasks' },
    ],
    tools: [],
  }, {
    conversationId: 'conversation-1',
    suggestUuidByToolCallId,
  });

  assert.equal(result.messages[0].suggestUuid, 'suggest-1');
  assert.equal(result.messages[0].tool_calls[0].function.name, 'TaskList');
  assert.deepEqual(result.messages[1], {
    content: 'No tasks',
    multiModalContent: [{ type: 'text', text: 'No tasks' }],
    role: 'tool',
    triggerMode: 'AGENT',
    suggestUuid: 'suggest-1',
    tool_call_id: 'call_1',
    tool_call_name: 'TaskList',
  });
});

test('normalizeCatpawAgentChunk maps top-level content and toolCalls to OpenAI deltas', () => {
  const result = normalizeCatpawAgentChunk({
    id: 'chatcmpl-1',
    content: '',
    suggestUuid: 'suggest-1',
    toolCalls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'TaskList', arguments: '{}' },
    }],
    choices: [{ finishReason: 'tool_calls' }],
    lastOne: true,
    statusCode: 0,
  });

  assert.deepEqual(result.choices[0].delta.tool_calls, [{
    index: 0,
    id: 'call_1',
    type: 'function',
    function: { name: 'TaskList', arguments: '{}' },
  }]);
  assert.equal(result.choices[0].finish_reason, 'tool_calls');
});

test('normalizeCatpawAgentChunk does not resend retained text with a tool update', () => {
  const result = normalizeCatpawAgentChunk({
    id: 'chatcmpl-1',
    content: 'I will inspect the project.',
    toolCalls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'Glob', arguments: '{"pattern":"**/*"}' },
    }],
    choices: [{ delta: { toolCalls: [{}] } }],
    statusCode: 0,
  });

  assert.equal(result.choices[0].delta.content, undefined);
  assert.equal(result.choices[0].delta.tool_calls[0].function.name, 'Glob');
});

test('normalizeCatpawAgentChunk preserves omitted arguments in later tool snapshots', () => {
  const result = normalizeCatpawAgentChunk({
    id: 'chatcmpl-1',
    content: '',
    toolCalls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'Glob' },
    }],
    choices: [{ delta: { toolCalls: [{}] } }],
    statusCode: 0,
  });

  assert.equal(result.choices[0].delta.tool_calls[0].function.arguments, undefined);
});

test('normalizeCatpawAgentChunk rewrites complete native file tool paths', () => {
  const result = normalizeCatpawAgentChunk({
    id: 'chatcmpl-1',
    content: '',
    toolCalls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'Write', arguments: '{"file_path":"E:\\\\test1\\\\note.txt","content":"ok"}' },
    }],
    choices: [{ finishReason: 'tool_calls' }],
    statusCode: 0,
  }, workspaceContext());

  assert.deepEqual(JSON.parse(result.choices[0].delta.tool_calls[0].function.arguments), {
    file_path: '/sessions/session-one/mnt/test1/note.txt',
    content: 'ok',
  });
});

test('normalizeCatpawAgentChunk leaves partial file arguments unchanged', () => {
  const result = normalizeCatpawAgentChunk({
    id: 'chatcmpl-1',
    content: '',
    toolCalls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'Read', arguments: '{"file_path":"E:\\\\test1' },
    }],
    choices: [{ delta: { toolCalls: [{ function: { arguments: 'partial' } }] } }],
    statusCode: 0,
  }, workspaceContext());

  assert.equal(
    result.choices[0].delta.tool_calls[0].function.arguments,
    '{"file_path":"E:\\\\test1',
  );
});

test('normalizeCatpawAgentChunk preserves native file paths in host-loop sessions', () => {
  const context = { ...workspaceContext(), hostLoopMode: true };
  const result = normalizeCatpawAgentChunk({
    id: 'chatcmpl-1',
    content: '',
    toolCalls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'Write', arguments: '{"file_path":"E:\\\\test1\\\\note.txt","content":"ok"}' },
    }],
    choices: [{ finishReason: 'tool_calls' }],
    statusCode: 0,
  }, context);

  assert.deepEqual(JSON.parse(result.choices[0].delta.tool_calls[0].function.arguments), {
    file_path: 'E:\\test1\\note.txt',
    content: 'ok',
  });
});

test('summarizeCatpawToolCalls reports argument shape without argument content', () => {
  const summary = summarizeCatpawToolCalls({
    toolCalls: [{
      id: 'call_1',
      function: { name: 'Glob', arguments: '{"pattern":"secret"}' },
    }],
    choices: [{ delta: { toolCalls: [{ function: { arguments: 'partial' } }] } }],
  });

  assert.deepEqual(summary, [
    {
      source: 'topLevel',
      index: 0,
      id: 'call_1',
      name: 'Glob',
      argumentsPresent: true,
      argumentsType: 'string',
      argumentsChars: 20,
    },
    {
      source: 'deltaCamel',
      index: 0,
      id: null,
      name: null,
      argumentsPresent: true,
      argumentsType: 'string',
      argumentsChars: 7,
    },
  ]);
  assert.doesNotMatch(JSON.stringify(summary), /secret/);
});

test('CatpawAgentSessionStore keeps one conversation and records tool suggest UUIDs', () => {
  const store = new CatpawAgentSessionStore();
  const request = {
    messages: [
      { role: 'system', content: 'cwd: F:/project' },
      { role: 'user', content: 'Inspect the project' },
    ],
  };
  const first = store.get(request);
  const second = store.get(structuredClone(request));

  assert.equal(first.conversationId, second.conversationId);
  store.record(first, {
    suggestUuid: 'suggest-1',
    toolCalls: [{ id: 'call_1' }],
  });
  assert.equal(first.suggestUuidByToolCallId.get('call_1'), 'suggest-1');
});

test('CatpawAgentSessionStore rotates long-running upstream conversations', () => {
  const store = new CatpawAgentSessionStore({ maxRequestsPerConversation: 2 });
  const request = sessionRequest('long-task');
  const first = store.get(request);
  const firstConversationId = first.conversationId;
  store.record(first, {
    suggestUuid: 'suggest-old',
    toolCalls: [{ id: 'call-old' }],
  });

  const second = store.get(request);
  assert.equal(second.conversationId, firstConversationId);
  assert.equal(second.requestCount, 2);

  const third = store.get(request);
  assert.notEqual(third.conversationId, firstConversationId);
  assert.equal(third.requestCount, 1);
  assert.equal(third.rotationCount, 1);
  assert.equal(third.suggestUuidByToolCallId.size, 0);
});

test('normalizeCatpawAgentChunk removes leaked think tags from text', () => {
  const normalized = normalizeCatpawAgentChunk({
    content: 'plan</think>continue<think>details<tool_call>shell_command</tool_call>',
    lastOne: true,
  });
  assert.equal(normalized.choices[0].delta.content, 'plancontinuedetails');
});

test('CatpawAgentSessionStore keeps conversation and mappings when access is refreshed', () => {
  let now = 0;
  const store = new CatpawAgentSessionStore({ now: () => now });
  const request = sessionRequest('project-a');
  const first = store.get(request);
  store.record(first, {
    suggestUuid: 'suggest-1',
    toolCalls: [{ id: 'call_1' }],
  });

  now = 10;
  const second = store.get(structuredClone(request));

  assert.equal(second, first);
  assert.equal(second.conversationId, first.conversationId);
  assert.equal(second.suggestUuidByToolCallId.get('call_1'), 'suggest-1');
  assert.equal(second.lastAccessAt, 10);
  assert.equal(store.size, 1);
});

test('CatpawAgentSessionStore defaults to a monotonic in-process clock', () => {
  const before = performance.now();
  const session = new CatpawAgentSessionStore().get(sessionRequest('project-a'));
  const after = performance.now();

  assert.ok(session.lastAccessAt >= before);
  assert.ok(session.lastAccessAt <= after);
});

test('CatpawAgentSessionStore samples one logical instant per get at the TTL boundary', () => {
  let readings = [0];
  let readCount = 0;
  const store = new CatpawAgentSessionStore({
    ttlMs: 100,
    now: () => {
      readCount += 1;
      return readings.length > 1 ? readings.shift() : readings[0];
    },
  });
  const request = sessionRequest('project-a');
  const session = store.get(request);

  readings = [99, 100];
  readCount = 0;
  const refreshed = store.get(request);

  assert.equal(refreshed, session);
  assert.equal(readCount, 1);
  assert.equal(refreshed.lastAccessAt, 99);
});

test('CatpawAgentSessionStore evicts the least recently used session', () => {
  let now = 0;
  const store = new CatpawAgentSessionStore({
    maxSessions: 2,
    ttlMs: 1_000,
    now: () => now,
  });
  const requestA = sessionRequest('project-a');
  const requestB = sessionRequest('project-b');
  const requestC = sessionRequest('project-c');
  const sessionA = store.get(requestA);
  const sessionB = store.get(requestB);

  now = 1;
  assert.equal(store.get(requestA), sessionA);
  now = 2;
  store.get(requestC);

  assert.equal(store.size, 2);
  assert.equal(store.get(requestA), sessionA);
  assert.notEqual(store.get(requestB).conversationId, sessionB.conversationId);
  assert.equal(store.size, 2);
});

test('CatpawAgentSessionStore uses insertion order for LRU with a constant clock', () => {
  const store = new CatpawAgentSessionStore({
    maxSessions: 2,
    ttlMs: 100,
    now: () => 0,
  });
  const requestA = sessionRequest('project-a');
  const requestB = sessionRequest('project-b');
  const requestC = sessionRequest('project-c');
  const sessionA = store.get(requestA);
  const sessionB = store.get(requestB);

  store.get(requestA);
  store.get(requestC);

  assert.equal(store.get(requestA), sessionA);
  assert.notEqual(store.get(requestB).conversationId, sessionB.conversationId);
});

test('CatpawAgentSessionStore expires sessions at the TTL boundary', () => {
  let now = 0;
  const store = new CatpawAgentSessionStore({ ttlMs: 100, now: () => now });
  const request = sessionRequest('project-a');
  const first = store.get(request);

  now = 99;
  assert.equal(store.get(request), first);
  now = 199;
  assert.notEqual(store.get(request).conversationId, first.conversationId);
});

test('CatpawAgentSessionStore sweep returns the number of expired sessions removed', () => {
  let now = 0;
  const store = new CatpawAgentSessionStore({ ttlMs: 100, now: () => now });
  store.get(sessionRequest('project-a'));
  now = 50;
  store.get(sessionRequest('project-b'));

  now = 100;
  assert.equal(store.sweep(), 1);
  assert.equal(store.size, 1);
  assert.equal(store.sweep(), 0);
});

test('CatpawAgentSessionStore refreshed access survives a later sweep', () => {
  let now = 0;
  const store = new CatpawAgentSessionStore({ ttlMs: 100, now: () => now });
  const request = sessionRequest('project-a');
  const session = store.get(request);

  now = 60;
  store.get(request);
  now = 100;

  assert.equal(store.sweep(), 0);
  assert.equal(store.get(request), session);
});

test('CatpawAgentSessionStore caps suggest mappings and refreshes existing IDs', () => {
  const store = new CatpawAgentSessionStore({ maxSuggestMappings: 2 });
  const session = store.get(sessionRequest('project-a'));
  store.record(session, {
    suggestUuid: 'suggest-1',
    toolCalls: [{ id: 'call_1' }, { id: 'call_2' }],
  });
  store.record(session, {
    suggestUuid: 'suggest-2',
    toolCalls: [{ id: 'call_1' }],
  });
  store.record(session, {
    suggestUuid: 'suggest-3',
    toolCalls: [{ id: 'call_3' }],
  });

  assert.deepEqual(
    [...session.suggestUuidByToolCallId.entries()],
    [['call_1', 'suggest-2'], ['call_3', 'suggest-3']],
  );
});

test('CatpawAgentSessionStore retains only newest mappings from one large chunk', () => {
  const store = new CatpawAgentSessionStore({ maxSuggestMappings: 5 });
  const session = store.get(sessionRequest('project-a'));
  store.record(session, {
    suggestUuid: 'suggest-bulk',
    toolCalls: Array.from({ length: 50 }, (_, index) => ({ id: `call_${index}` })),
  });

  assert.deepEqual(
    [...session.suggestUuidByToolCallId.entries()],
    Array.from({ length: 5 }, (_, index) => [`call_${index + 45}`, 'suggest-bulk']),
  );
});

test('CatpawAgentSessionStore validates constructor options', async (t) => {
  for (const option of ['maxSessions', 'ttlMs', 'maxSuggestMappings']) {
    await t.test(`${option} must be a number`, () => {
      assert.throws(
        () => new CatpawAgentSessionStore({ [option]: '1' }),
        { name: 'TypeError', message: `${option} must be a number` },
      );
    });

    for (const value of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
      await t.test(`${option} rejects ${value}`, () => {
        assert.throws(
          () => new CatpawAgentSessionStore({ [option]: value }),
          { name: 'RangeError', message: `${option} must be a positive safe integer` },
        );
      });
    }
  }

  await t.test('now must be a function', () => {
    assert.throws(
      () => new CatpawAgentSessionStore({ now: Date.now() }),
      { name: 'TypeError', message: 'now must be a function' },
    );
  });
});

test('CatpawAgentSessionStore rejects invalid custom clock results', () => {
  for (const value of [NaN, Infinity, -Infinity, '100', null]) {
    const store = new CatpawAgentSessionStore({ now: () => value });
    assert.throws(
      () => store.get(sessionRequest('project-a')),
      { name: 'TypeError', message: 'now must return a finite number' },
    );
  }
});

function sessionRequest(project) {
  return {
    messages: [
      { role: 'system', content: `cwd: F:/${project}` },
      { role: 'user', content: 'Inspect the project' },
    ],
  };
}

function workspaceContext() {
  return {
    mappings: [{
      hostRoot: 'E:\\test1',
      mountRoot: '/sessions/session-one/mnt/test1',
    }],
  };
}

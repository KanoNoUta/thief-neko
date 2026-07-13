import test from 'node:test';
import assert from 'node:assert/strict';
import {
  anthropicToOpenAIRequest,
  normalizeOpenAIResponse,
  openAIToAnthropicMessage,
  parseToolArguments,
  prepareOpenAIRequestForCatpaw,
} from '../src/converters.js';

test('parseToolArguments repairs only missing JSON container closers', () => {
  assert.deepEqual(
    parseToolArguments('{"path":"README.md","range":[1,2]'),
    { path: 'README.md', range: [1, 2] },
  );
  assert.deepEqual(
    parseToolArguments('{"path":"unfinished'),
    { raw: '{"path":"unfinished' },
  );
  assert.deepEqual(
    parseToolArguments('{"path":}'),
    { raw: '{"path":}' },
  );
});

test('prepareOpenAIRequestForCatpaw injects tool protocol guidance for OpenAI clients', () => {
  const result = prepareOpenAIRequestForCatpaw({
    model: 'glm-5.2',
    messages: [{ role: 'user', content: 'Inspect files' }],
    tools: [{ type: 'function', function: { name: 'TaskList', parameters: { type: 'object' } } }],
  }, { maxSystemChars: 1000 });

  assert.equal(result.messages[0].role, 'system');
  assert.match(result.messages[0].content, /using tool_calls/);
  assert.equal(result.messages[1].content, 'Inspect files');
});

test('anthropicToOpenAIRequest maps system, text messages, model, and max_tokens', () => {
  const result = anthropicToOpenAIRequest({
    model: 'claude-sonnet-4',
    max_tokens: 1000,
    system: 'You are concise.',
    messages: [
      { role: 'user', content: [{ type: 'text', text: 'Hello' }] },
    ],
  }, { model: 'glm-4.5' });

  assert.equal(result.model, 'glm-4.5');
  assert.equal(result.max_tokens, 1000);
  assert.deepEqual(result.messages, [
    { role: 'system', content: 'You are concise.' },
    { role: 'user', content: 'Hello' },
  ]);
});

test('anthropicToOpenAIRequest maps tools to OpenAI functions', () => {
  const result = anthropicToOpenAIRequest({
    max_tokens: 256,
    messages: [{ role: 'user', content: 'read file' }],
    tools: [{
      name: 'read_file',
      description: 'Read a file',
      input_schema: { type: 'object', properties: { path: { type: 'string' } } },
    }],
  }, { model: 'glm-4.5' });

  assert.equal(result.tools[0].type, 'function');
  assert.equal(result.tools[0].function.name, 'read_file');
  assert.equal(result.tool_choice, 'required');
});

test('anthropicToOpenAIRequest allows a final answer after tool results', () => {
  const result = anthropicToOpenAIRequest({
    messages: [{
      role: 'user',
      content: [{ type: 'tool_result', tool_use_id: 'toolu_1', content: 'done' }],
    }],
    tools: [{ name: 'TaskList', input_schema: { type: 'object' } }],
  }, { model: 'glm-5.2' });

  assert.equal(result.tool_choice, 'auto');
});

test('anthropicToOpenAIRequest compacts oversized host instructions', () => {
  const result = anthropicToOpenAIRequest({
    system: `${'A'.repeat(3000)}${'Z'.repeat(1000)}`,
    messages: [{ role: 'user', content: 'hello' }],
    tools: [{
      name: 'large_tool',
      description: 'D'.repeat(500),
      input_schema: { type: 'object', properties: { value: { type: 'string' } } },
    }],
  }, {
    model: 'glm-5.2',
    maxSystemChars: 1000,
    maxToolDescriptionChars: 100,
  });

  assert.equal(result.messages[0].content.length, 1000);
  assert.match(result.messages[0].content, /^A+/);
  assert.match(result.messages[0].content, /Z+/);
  assert.match(result.messages[0].content, /host instructions compacted/);
  assert.match(result.messages[0].content, /using tool_calls/);
  assert.match(result.messages[0].content, /task is complete\./);
  assert.match(result.messages[0].content, /Do not rewrite Bash command strings\.$/);
  assert.equal(result.tools[0].function.description.length, 100);
  assert.deepEqual(result.tools[0].function.parameters, {
    type: 'object',
    properties: { value: { type: 'string' } },
  });
});

test('anthropicToOpenAIRequest includes Claude Desktop workspace path guidance for tools', () => {
  const result = anthropicToOpenAIRequest({
    system: 'Work carefully.',
    messages: [{ role: 'user', content: 'write a file' }],
    tools: [{ name: 'Write', input_schema: { type: 'object' } }],
  }, {
    model: 'glm-5.2',
    workspaceContext: {
      mappings: [{
        hostRoot: 'E:\\test1',
        mountRoot: '/sessions/blissful-adoring-planck/mnt/test1',
      }],
    },
  });

  assert.match(result.messages[0].content, /Native file tools run inside the local-agent VM/);
  assert.match(result.messages[0].content, /\$PWD\/mnt/);
  assert.match(
    result.messages[0].content,
    /E:\\test1 => \/sessions\/blissful-adoring-planck\/mnt\/test1/,
  );
});

test('anthropicToOpenAIRequest keeps workspace mapping after system compaction', () => {
  const result = anthropicToOpenAIRequest({
    system: 'A'.repeat(4000),
    messages: [{ role: 'user', content: 'write a file' }],
    tools: [{ name: 'Write', input_schema: { type: 'object' } }],
  }, {
    model: 'glm-5.2',
    maxSystemChars: 900,
    workspaceContext: {
      mappings: [{
        hostRoot: 'E:\\test1',
        mountRoot: '/sessions/blissful-adoring-planck/mnt/test1',
      }],
    },
  });

  assert.equal(result.messages[0].content.length, 900);
  assert.match(result.messages[0].content, /host instructions compacted/);
  assert.match(
    result.messages[0].content,
    /E:\\test1 => \/sessions\/blissful-adoring-planck\/mnt\/test1/,
  );
});

test('anthropicToOpenAIRequest gives host-loop native tools Windows paths', () => {
  const result = anthropicToOpenAIRequest({
    messages: [{ role: 'user', content: 'write a file' }],
    tools: [{ name: 'Write', input_schema: { type: 'object' } }],
  }, {
    model: 'glm-5.2',
    workspaceContext: {
      hostLoopMode: true,
      mappings: [{
        hostRoot: 'E:\\test1',
        mountRoot: '/sessions/host-session/mnt/test1',
      }],
    },
  });

  assert.match(result.messages[0].content, /Native Read\/Write\/Edit\/Grep\/Glob tools run on the Windows host/);
  assert.match(result.messages[0].content, /Use this exact path with native file tools: E:\\test1/);
  assert.match(result.messages[0].content, /mcp__workspace__bash only: \/sessions\/host-session\/mnt\/test1/);
  assert.doesNotMatch(result.messages[0].content, /prefer their \/sessions/);
});

test('anthropicToOpenAIRequest maps assistant tool_use blocks and user tool_result blocks', () => {
  const result = anthropicToOpenAIRequest({
    max_tokens: 256,
    messages: [
      {
        role: 'assistant',
        content: [
          { type: 'text', text: 'I will read it.' },
          { type: 'tool_use', id: 'toolu_1', name: 'read_file', input: { path: 'README.md' } },
        ],
      },
      {
        role: 'user',
        content: [
          { type: 'tool_result', tool_use_id: 'toolu_1', content: 'hello' },
        ],
      },
    ],
  }, { model: 'glm-4.5' });

  assert.deepEqual(result.messages, [
    {
      role: 'assistant',
      content: 'I will read it.',
      tool_calls: [{
        id: 'toolu_1',
        type: 'function',
        function: { name: 'read_file', arguments: '{"path":"README.md"}' },
      }],
    },
    { role: 'tool', tool_call_id: 'toolu_1', content: 'hello' },
  ]);
});

test('openAIToAnthropicMessage maps text responses', () => {
  const result = openAIToAnthropicMessage({
    id: 'chatcmpl-1',
    choices: [{ finish_reason: 'stop', message: { role: 'assistant', content: 'Done' } }],
    usage: { prompt_tokens: 3, completion_tokens: 2 },
  }, 'glm-4.5');

  assert.equal(result.type, 'message');
  assert.equal(result.role, 'assistant');
  assert.deepEqual(result.content, [{ type: 'text', text: 'Done' }]);
  assert.equal(result.stop_reason, 'end_turn');
  assert.deepEqual(result.usage, { input_tokens: 3, output_tokens: 2 });
});

test('openAIToAnthropicMessage maps tool call responses', () => {
  const result = openAIToAnthropicMessage({
    id: 'chatcmpl-2',
    choices: [{
      finish_reason: 'tool_calls',
      message: {
        role: 'assistant',
        content: null,
        tool_calls: [{
          id: 'call_1',
          type: 'function',
          function: { name: 'read_file', arguments: '{"path":"README.md"}' },
        }],
      },
    }],
  }, 'glm-4.5');

  assert.deepEqual(result.content, [{
    type: 'tool_use',
    id: 'call_1',
    name: 'read_file',
    input: { path: 'README.md' },
  }]);
  assert.equal(result.stop_reason, 'tool_use');
});

test('normalizeOpenAIResponse unwraps Catpaw data envelopes', () => {
  const response = normalizeOpenAIResponse({
    status: 0,
    data: {
      id: 'chatcmpl-3',
      choices: [{ finish_reason: 'stop', message: { content: 'ok' } }],
    },
  });

  assert.equal(response.id, 'chatcmpl-3');
});

test('normalizeOpenAIResponse throws on Catpaw logical auth errors', () => {
  assert.throws(() => normalizeOpenAIResponse({
    status: 401,
    data: { message: 'auth failed', type: 'passport' },
  }), /auth failed/);
});

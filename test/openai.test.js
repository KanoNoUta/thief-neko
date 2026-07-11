import test from 'node:test';
import assert from 'node:assert/strict';
import { OpenAIStreamAccumulator, normalizeOpenAIRequest } from '../src/openai.js';

test('normalizeOpenAIRequest pins the configured model and preserves tools', () => {
  const tools = [{
    type: 'function',
    function: { name: 'TaskList', parameters: { type: 'object' } },
  }];
  const result = normalizeOpenAIRequest({
    model: 'client-alias',
    stream: true,
    messages: [{ role: 'user', content: 'List tasks' }],
    tools,
  }, { model: 'glm-5.2' });

  assert.equal(result.model, 'glm-5.2');
  assert.equal(result.stream, true);
  assert.deepEqual(result.tools, tools);
});

test('OpenAIStreamAccumulator collapses native snapshots and builds a tool response', () => {
  const stream = new OpenAIStreamAccumulator('glm-5.2', { collapseSnapshots: true });
  const first = stream.ingest({
    id: 'chatcmpl-1',
    choices: [{
      delta: {
        content: 'Inspect.Inspect.',
        tool_calls: [{
          index: 0,
          id: 'call_1',
          type: 'function',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
      finish_reason: null,
    }],
  });
  const repeated = stream.ingest({
    id: 'chatcmpl-1',
    choices: [{
      delta: {
        content: 'Inspect.Inspect.',
        tool_calls: [{
          index: 0,
          id: 'call_1',
          type: 'function',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
      finish_reason: 'tool_calls',
    }],
  });

  assert.equal(first.choices[0].delta.content, 'Inspect.');
  assert.equal(first.choices[0].delta.tool_calls[0].function.arguments, '{}');
  assert.equal(repeated.choices[0].delta.content, undefined);
  assert.equal(repeated.choices[0].delta.tool_calls, undefined);
  assert.equal(repeated.choices[0].finish_reason, 'tool_calls');
  assert.deepEqual(stream.response().choices[0].message, {
    role: 'assistant',
    content: 'Inspect.',
    tool_calls: [{
      id: 'call_1',
      type: 'function',
      function: { name: 'TaskList', arguments: '{}' },
    }],
  });
});

test('OpenAIStreamAccumulator appends ordinary incremental text', () => {
  const stream = new OpenAIStreamAccumulator('glm-5.2');
  stream.ingest({ id: 'chatcmpl-2', choices: [{ delta: { content: 'Hel' } }] });
  stream.ingest({
    id: 'chatcmpl-2',
    choices: [{ delta: { content: 'lo' }, finish_reason: 'stop' }],
    usage: { prompt_tokens: 3, completion_tokens: 2 },
  });

  assert.equal(stream.response().choices[0].message.content, 'Hello');
  assert.deepEqual(stream.response().usage, { prompt_tokens: 3, completion_tokens: 2, total_tokens: 5 });
});

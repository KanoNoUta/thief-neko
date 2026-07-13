import test from 'node:test';
import assert from 'node:assert/strict';
import {
  AnthropicStreamBuilder,
  catpawStreamChunkToOpenAI,
  formatSseEvent,
  openAIStreamChunksToAnthropicEvents,
  parseOpenAISseChunk,
} from '../src/streaming.js';

function assertStreamBufferLimit(error, maximum) {
  assert.ok(error instanceof RangeError);
  assert.equal(error.code, 'CATAPI_STREAM_BUFFER_LIMIT');
  assert.match(error.message, new RegExp(`maximum of ${maximum} retained characters`));
  return true;
}

test('catpawStreamChunkToOpenAI turns Catpaw stream errors into visible text', () => {
  const chunk = catpawStreamChunkToOpenAI({
    msg: 'request too large',
    lastOne: true,
    statusCode: 400,
  });

  assert.equal(chunk.choices[0].delta.content, '[Catpaw 400] request too large');
  assert.equal(chunk.choices[0].finish_reason, 'stop');
});

test('openAIStreamChunksToAnthropicEvents emits Anthropic text stream events', () => {
  const events = openAIStreamChunksToAnthropicEvents([
    { id: 'chatcmpl-1', choices: [{ delta: { content: 'Hel' }, finish_reason: null }] },
    { id: 'chatcmpl-1', choices: [{ delta: { content: 'lo' }, finish_reason: null }] },
    { id: 'chatcmpl-1', choices: [{ delta: {}, finish_reason: 'stop' }] },
  ], 'glm-4.5');

  assert.equal(events[0].event, 'message_start');
  assert.equal(events[1].event, 'content_block_start');
  assert.deepEqual(events[2], {
    event: 'content_block_delta',
    data: { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'Hel' } },
  });
  assert.equal(events.at(-1).event, 'message_stop');
});

test('openAIStreamChunksToAnthropicEvents accepts Catpaw finishReason casing', () => {
  const events = openAIStreamChunksToAnthropicEvents([
    { id: 'chatcmpl-1', choices: [{ delta: { content: 'OK' }, finishReason: 'stop' }] },
  ], 'glm-5.2');

  assert.equal(events.at(-2).data.delta.stop_reason, 'end_turn');
});

test('openAIStreamChunksToAnthropicEvents collapses exact repeated Catpaw Agent text', () => {
  const events = openAIStreamChunksToAnthropicEvents([
    { id: 'chatcmpl-1', choices: [{ delta: { content: 'Inspect the project.' }, finish_reason: null }] },
    { id: 'chatcmpl-1', choices: [{ delta: { content: 'Inspect the project.' }, finish_reason: 'stop' }] },
  ], 'glm-5.2', { collapseRepeatedText: true });

  const text = events
    .filter((event) => event.event === 'content_block_delta')
    .map((event) => event.data.delta.text)
    .join('');
  assert.equal(text, 'Inspect the project.');
});

test('openAIStreamChunksToAnthropicEvents buffers streamed tool calls', () => {
  const events = openAIStreamChunksToAnthropicEvents([
    {
      id: 'chatcmpl-2',
      choices: [{
        delta: { tool_calls: [{ index: 0, id: 'call_1', function: { name: 'read_file', arguments: '{"path"' } }] },
        finish_reason: null,
      }],
    },
    {
      id: 'chatcmpl-2',
      choices: [{
        delta: { tool_calls: [{ index: 0, function: { arguments: ':"README.md"}' } }] },
        finish_reason: 'tool_calls',
      }],
    },
  ], 'glm-4.5');

  const toolStart = events.find((event) => event.event === 'content_block_start');
  assert.deepEqual(toolStart.data.content_block, {
    type: 'tool_use',
    id: 'call_1',
    name: 'read_file',
    input: {},
  });
  const inputDelta = events.find((event) => (
    event.event === 'content_block_delta'
    && event.data.delta.type === 'input_json_delta'
  ));
  assert.deepEqual(inputDelta.data.delta, {
    type: 'input_json_delta',
    partial_json: '{"path":"README.md"}',
  });
  assert.equal(events.at(-2).data.delta.stop_reason, 'tool_use');
});

test('openAIStreamChunksToAnthropicEvents repairs missing tool argument closers', () => {
  const events = openAIStreamChunksToAnthropicEvents([{
    id: 'chatcmpl-repair',
    choices: [{
      delta: {
        tool_calls: [{
          index: 0,
          id: 'call_read',
          function: { name: 'Read', arguments: '{"file_path":"F:\\\\project\\\\README.md"' },
        }],
      },
      finish_reason: 'tool_calls',
    }],
  }], 'glm-5.2');

  const inputDelta = events.find((event) => event.data?.delta?.type === 'input_json_delta');
  assert.deepEqual(JSON.parse(inputDelta.data.delta.partial_json), {
    file_path: 'F:\\project\\README.md',
  });
  assert.doesNotMatch(inputDelta.data.delta.partial_json, /"raw"/);
});

test('openAIStreamChunksToAnthropicEvents replaces repeated native tool snapshots', () => {
  const snapshot = {
    id: 'chatcmpl-3',
    choices: [{
      delta: {
        tool_calls: [{
          index: 0,
          id: 'call_1',
          type: 'function',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
      finish_reason: null,
    }],
  };
  const events = openAIStreamChunksToAnthropicEvents([
    snapshot,
    {
      ...snapshot,
      choices: [{
        ...snapshot.choices[0],
        finish_reason: 'tool_calls',
      }],
    },
  ], 'glm-5.2');

  const toolStart = events.find((event) => (
    event.event === 'content_block_start'
    && event.data.content_block.type === 'tool_use'
  ));
  assert.equal(toolStart.data.content_block.name, 'TaskList');
  assert.deepEqual(toolStart.data.content_block.input, {});
});

test('AnthropicStreamBuilder validates maxBufferChars', () => {
  assert.equal(
    new AnthropicStreamBuilder('glm-5.2').maxBufferChars,
    4 * 1024 * 1024,
  );
  assert.equal(new AnthropicStreamBuilder('glm-5.2', { maxBufferChars: 1 }).maxBufferChars, 1);

  for (const maxBufferChars of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, Infinity, NaN, '10', null]) {
    assert.throws(
      () => new AnthropicStreamBuilder('glm-5.2', { maxBufferChars }),
      { name: 'RangeError', message: /maxBufferChars must be a positive safe integer/ },
    );
  }
});

test('openAIStreamChunksToAnthropicEvents rejects collapsed text retained over the limit', () => {
  const chunks = Array.from({ length: 3 }, (_, index) => ({
    id: 'chatcmpl-limit',
    choices: [{
      delta: { content: '12345678' },
      finish_reason: index === 2 ? 'stop' : null,
    }],
  }));

  assert.throws(
    () => openAIStreamChunksToAnthropicEvents(chunks, 'glm-5.2', {
      collapseRepeatedText: true,
      maxBufferChars: 16,
    }),
    (error) => assertStreamBufferLimit(error, 16),
  );
});

test('openAIStreamChunksToAnthropicEvents rejects emitted text retained over the limit', () => {
  assert.throws(
    () => openAIStreamChunksToAnthropicEvents([
      { id: 'chatcmpl-limit', choices: [{ delta: { content: 'abc' }, finish_reason: null }] },
      { id: 'chatcmpl-limit', choices: [{ delta: { content: 'def' }, finish_reason: 'stop' }] },
    ], 'glm-5.2', { maxBufferChars: 5 }),
    (error) => assertStreamBufferLimit(error, 5),
  );
});

test('openAIStreamChunksToAnthropicEvents rejects retained tool arguments over the limit', () => {
  assert.throws(
    () => openAIStreamChunksToAnthropicEvents([{
      id: 'chatcmpl-limit',
      choices: [{
        delta: { tool_calls: [{ index: 0, function: { arguments: '123456' } }] },
        finish_reason: null,
      }],
    }], 'glm-5.2', { maxBufferChars: 5 }),
    (error) => assertStreamBufferLimit(error, 5),
  );
});

test('replacing the same native tool snapshot charges only the retained-size delta', () => {
  const snapshot = {
    id: 'chatcmpl-snapshot',
    choices: [{
      delta: {
        tool_calls: [{
          index: 0,
          id: 'call_1',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
      finish_reason: null,
    }],
  };

  const events = openAIStreamChunksToAnthropicEvents([
    snapshot,
    {
      ...snapshot,
      choices: [{ ...snapshot.choices[0], finish_reason: 'tool_calls' }],
    },
  ], 'glm-5.2', { maxBufferChars: 16 });

  const toolStart = events.find((event) => event.data?.content_block?.type === 'tool_use');
  assert.equal(toolStart.data.content_block.name, 'TaskList');
});

test('captures OpenAI usage from the first chunk', () => {
  const events = openAIStreamChunksToAnthropicEvents([{
    id: 'chatcmpl-usage',
    usage: { prompt_tokens: 3, completion_tokens: 2 },
    choices: [{ delta: { content: 'OK' }, finish_reason: 'stop' }],
  }], 'glm-5.2');

  assert.deepEqual(events[0].data.message.usage, { input_tokens: 3, output_tokens: 2 });
  assert.deepEqual(events.at(-2).data.usage, { output_tokens: 2 });
});

test('late Anthropic usage updates the returned message_start and message_delta', () => {
  const events = openAIStreamChunksToAnthropicEvents([
    { id: 'chatcmpl-usage', choices: [{ delta: { content: 'OK' }, finish_reason: null }] },
    {
      id: 'chatcmpl-usage',
      usage: { input_tokens: 5, output_tokens: 4 },
      choices: [{ delta: {}, finish_reason: 'stop' }],
    },
  ], 'glm-5.2');

  assert.deepEqual(events[0].data.message.usage, { input_tokens: 5, output_tokens: 4 });
  assert.deepEqual(events.at(-2).data.usage, { output_tokens: 4 });
});

test('invalid or missing usage values preserve prior numeric usage', () => {
  const events = openAIStreamChunksToAnthropicEvents([
    {
      id: 'chatcmpl-usage',
      usage: { prompt_tokens: 3.5, completion_tokens: 4 },
      choices: [{ delta: {}, finish_reason: null }],
    },
    {
      id: 'chatcmpl-usage',
      usage: { input_tokens: -1, output_tokens: 5 },
      choices: [{ delta: {}, finish_reason: null }],
    },
    {
      id: 'chatcmpl-usage',
      usage: { prompt_tokens: NaN },
      choices: [{ delta: {}, finish_reason: null }],
    },
    {
      id: 'chatcmpl-usage',
      usage: { input_tokens: '6', output_tokens: Infinity },
      choices: [{ delta: {}, finish_reason: 'stop' }],
    },
  ], 'glm-5.2');

  assert.deepEqual(events[0].data.message.usage, { input_tokens: 3.5, output_tokens: 5 });
  assert.deepEqual(events.at(-2).data.usage, { output_tokens: 5 });
});

test('parseOpenAISseChunk parses data lines and ignores done marker', () => {
  const parsed = parseOpenAISseChunk('data: {"id":"1","choices":[]}\n\ndata: [DONE]\n\n');

  assert.deepEqual(parsed, [{ id: '1', choices: [] }]);
});

test('formatSseEvent serializes Anthropic events', () => {
  assert.equal(
    formatSseEvent({ event: 'ping', data: { type: 'ping' } }),
    'event: ping\ndata: {"type":"ping"}\n\n',
  );
});

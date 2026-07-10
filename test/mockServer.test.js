import assert from 'node:assert/strict';
import test from 'node:test';
import { buildMockMessage, createMockServer, summarizeAnthropicRequest } from '../src/mockServer.js';

test('buildMockMessage drives a local tool loop', () => {
  const first = buildMockMessage({
    model: 'claude-fable-5',
    messages: [{ role: 'user', content: [{ type: 'text', text: 'LOCAL_TOOL_TEST' }] }],
    tools: [{ name: 'TaskList', input_schema: { type: 'object' } }],
  });

  assert.equal(first.stop_reason, 'tool_use');
  assert.deepEqual(first.content[0], {
    type: 'tool_use',
    id: 'toolu_mock_task_list',
    name: 'TaskList',
    input: {},
  });

  const second = buildMockMessage({
    model: 'claude-fable-5',
    messages: [{
      role: 'user',
      content: [{ type: 'tool_result', tool_use_id: 'toolu_mock_task_list', content: 'No tasks' }],
    }],
  });

  assert.equal(second.stop_reason, 'end_turn');
  assert.deepEqual(second.content, [{ type: 'text', text: 'LOCAL_TOOL_LOOP_OK' }]);
});

test('summarizeAnthropicRequest reports sizes without prompt text', () => {
  const summary = summarizeAnthropicRequest({
    system: [{ type: 'text', text: 'secret system' }],
    messages: [{ role: 'user', content: [{ type: 'text', text: 'secret user' }] }],
    tools: [{ name: 'Read', description: 'read files', input_schema: { type: 'object' } }],
  });

  assert.equal(summary.systemChars, 13);
  assert.deepEqual(summary.messages, [{ role: 'user', contentChars: 11, blockTypes: ['text'] }]);
  assert.equal(summary.tools[0].name, 'Read');
  assert.equal(summary.tools[0].descriptionChars, 10);
  assert.doesNotMatch(JSON.stringify(summary), /secret/);
});

test('mock server returns a complete Anthropic text stream', async (t) => {
  const server = createMockServer();
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());

  const { port } = server.address();
  const response = await fetch(`http://127.0.0.1:${port}/v1/messages?beta=true`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ model: 'claude-fable-5', stream: true, messages: [] }),
  });
  const body = await response.text();

  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type'), /^text\/event-stream/);
  assert.match(body, /"type":"message_start"/);
  assert.match(body, /"type":"text_delta","text":"LOCAL_MOCK_OK"/);
  assert.match(body, /"stop_reason":"end_turn"/);
  assert.match(body, /"type":"message_stop"/);
});

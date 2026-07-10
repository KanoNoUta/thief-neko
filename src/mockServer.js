import http from 'node:http';
import { pathToFileURL } from 'node:url';
import { formatSseEvent } from './streaming.js';

export function createMockServer() {
  return http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host || '127.0.0.1'}`);

    if (req.method === 'GET' && url.pathname === '/health') {
      sendJson(res, 200, { ok: true, mode: 'mock' });
      return;
    }

    if (req.method !== 'POST' || url.pathname !== '/v1/messages') {
      sendJson(res, 404, { type: 'error', error: { type: 'not_found_error', message: 'Not found' } });
      return;
    }

    const request = await readJson(req);
    console.log(JSON.stringify({ type: 'mock_request', ...summarizeAnthropicRequest(request) }));
    const message = buildMockMessage(request);

    if (!request.stream) {
      sendJson(res, 200, message);
      return;
    }

    res.writeHead(200, {
      'content-type': 'text/event-stream; charset=utf-8',
      'cache-control': 'no-cache, no-transform',
      connection: 'keep-alive',
    });
    res.write(formatSseEvent({
      event: 'message_start',
      data: {
        type: 'message_start',
        message: { ...message, content: [], stop_reason: null, stop_sequence: null },
      },
    }));
    message.content.forEach((block, index) => writeContentBlock(res, block, index));
    res.write(formatSseEvent({
      event: 'message_delta',
      data: {
        type: 'message_delta',
        delta: { stop_reason: message.stop_reason, stop_sequence: null },
        usage: { output_tokens: 1 },
      },
    }));
    res.end(formatSseEvent({ event: 'message_stop', data: { type: 'message_stop' } }));
  });
}

export function buildMockMessage(request) {
  const hasToolResult = (request.messages || []).some((message) => (
    Array.isArray(message.content)
    && message.content.some((block) => block?.type === 'tool_result')
  ));
  const wantsToolTest = (request.messages || []).some((message) => (
    contentText(message.content).includes('LOCAL_TOOL_TEST')
  ));
  const hasTaskList = (request.tools || []).some((tool) => tool.name === 'TaskList');

  let content = [{ type: 'text', text: 'LOCAL_MOCK_OK' }];
  let stopReason = 'end_turn';

  if (hasToolResult) {
    content = [{ type: 'text', text: 'LOCAL_TOOL_LOOP_OK' }];
  } else if (wantsToolTest && hasTaskList) {
    content = [{
      type: 'tool_use',
      id: 'toolu_mock_task_list',
      name: 'TaskList',
      input: {},
    }];
    stopReason = 'tool_use';
  }

  return {
    id: `msg_mock_${Date.now()}`,
    type: 'message',
    role: 'assistant',
    model: request.model || 'claude-fable-5',
    content,
    stop_reason: stopReason,
    stop_sequence: null,
    usage: { input_tokens: 1, output_tokens: 1 },
  };
}

function writeContentBlock(res, block, index) {
  res.write(formatSseEvent({
    event: 'content_block_start',
    data: {
      type: 'content_block_start',
      index,
      content_block: block.type === 'text' ? { type: 'text', text: '' } : block,
    },
  }));
  if (block.type === 'text' && block.text) {
    res.write(formatSseEvent({
      event: 'content_block_delta',
      data: { type: 'content_block_delta', index, delta: { type: 'text_delta', text: block.text } },
    }));
  }
  res.write(formatSseEvent({
    event: 'content_block_stop',
    data: { type: 'content_block_stop', index },
  }));
}

function contentText(content) {
  if (typeof content === 'string') {
    return content;
  }
  if (!Array.isArray(content)) {
    return '';
  }
  return content.map((block) => block?.text || '').join('\n');
}

export function summarizeAnthropicRequest(request) {
  return {
    bodyBytes: Buffer.byteLength(JSON.stringify(request)),
    systemChars: contentChars(request.system),
    messages: (request.messages || []).map((message) => ({
      role: message.role,
      contentChars: contentChars(message.content),
      blockTypes: Array.isArray(message.content)
        ? message.content.map((block) => block?.type || typeof block)
        : [typeof message.content],
    })),
    tools: (request.tools || []).map((tool) => ({
      name: tool.name,
      descriptionChars: tool.description?.length || 0,
      schemaBytes: Buffer.byteLength(JSON.stringify(tool.input_schema || {})),
    })),
  };
}

function contentChars(content) {
  if (typeof content === 'string') {
    return content.length;
  }
  if (!Array.isArray(content)) {
    return 0;
  }
  return content.reduce((total, block) => {
    if (typeof block === 'string') {
      return total + block.length;
    }
    if (typeof block?.text === 'string') {
      return total + block.text.length;
    }
    return total + contentChars(block?.content);
  }, 0);
}

async function readJson(req) {
  const chunks = [];
  for await (const chunk of req) {
    chunks.push(chunk);
  }
  return chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {};
}

function sendJson(res, status, payload) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(payload));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const host = process.env.MOCK_LISTEN_HOST || '127.0.0.1';
  const port = Number(process.env.MOCK_LISTEN_PORT || 3000);
  createMockServer().listen(port, host, () => {
    console.log(`anthropic mock listening on http://${host}:${port}`);
  });
}

import http from 'node:http';
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createGatewayServer, createCredentialProvider } from '../src/server.js';
import { CatpawCredentialManager } from '../src/catpawCredentials.js';
import { CredentialBroker } from '../src/credentialBroker.js';
import { loadConfig } from '../src/config.js';
import { UsageStore } from '../src/usageStore.js';

test('gateway relays an OpenAI Chat Completions tool loop', async (t) => {
  const upstreamRequests = [];
  const upstream = http.createServer(async (req, res) => {
    const body = await readJson(req);
    upstreamRequests.push(body);
    const hasToolResult = body.messages.some((message) => message.role === 'tool');
    const chunk = hasToolResult
      ? {
          id: 'chatcmpl-openai-2',
          content: 'OPENAI_TOOL_LOOP_OK',
          choices: [{ finishReason: 'stop' }],
          lastOne: true,
          statusCode: 0,
        }
      : {
          id: 'chatcmpl-openai-1',
          toolCalls: [{
            id: 'call_openai_1',
            type: 'function',
            function: { name: 'TaskList', arguments: '{}' },
          }],
          choices: [{ finishReason: 'tool_calls' }],
          lastOne: true,
          statusCode: 0,
        };
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify(chunk)}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createTestGateway(upstream, null);
  await listen(gateway);
  t.after(() => gateway.close());
  const url = `http://127.0.0.1:${gateway.address().port}/v1/chat/completions`;
  const tools = [{
    type: 'function',
    function: { name: 'TaskList', description: 'List tasks', parameters: { type: 'object' } },
  }];

  const first = await postJson(url, {
    model: 'glm-5.2',
    stream: false,
    messages: [{ role: 'user', content: 'List tasks' }],
    tools,
  });
  const firstBody = JSON.parse(first);
  assert.equal(firstBody.choices[0].finish_reason, 'tool_calls');
  assert.equal(firstBody.choices[0].message.tool_calls[0].function.name, 'TaskList');

  const second = await postJson(url, {
    model: 'glm-5.2',
    stream: true,
    messages: [
      { role: 'user', content: 'List tasks' },
      {
        role: 'assistant',
        content: null,
        tool_calls: [{
          id: 'call_openai_1',
          type: 'function',
          function: { name: 'TaskList', arguments: '{}' },
        }],
      },
      { role: 'tool', tool_call_id: 'call_openai_1', content: 'No tasks' },
    ],
    tools,
  });
  assert.match(second, /OPENAI_TOOL_LOOP_OK/);
  assert.match(second, /data: \[DONE\]/);
  assert.equal(upstreamRequests.length, 2);
  assert.equal(upstreamRequests[1].conversationId, upstreamRequests[0].conversationId);
  assert.equal(
    upstreamRequests[1].messages.find((message) => message.role === 'tool').tool_call_name,
    'TaskList',
  );
});

test('gateway relays an OpenAI Responses custom tool loop', async (t) => {
  const upstreamRequests = [];
  const upstream = http.createServer(async (req, res) => {
    const body = await readJson(req);
    upstreamRequests.push(body);
    const hasToolResult = body.messages.some((message) => message.role === 'tool');
    const chunk = hasToolResult
      ? {
          id: 'chatcmpl-responses-2',
          content: 'RESPONSES_TOOL_LOOP_OK',
          choices: [{ finishReason: 'stop' }],
          lastOne: true,
          statusCode: 0,
        }
      : {
          id: 'chatcmpl-responses-1',
          toolCalls: [{
            id: 'call_patch_1',
            type: 'function',
            function: { name: 'apply_patch', arguments: '{"input":"patch body"}' },
          }],
          choices: [{ finishReason: 'tool_calls' }],
          lastOne: true,
          statusCode: 0,
        };
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify(chunk)}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createTestGateway(upstream, null);
  await listen(gateway);
  t.after(() => gateway.close());
  const url = `http://127.0.0.1:${gateway.address().port}/v1/responses`;
  const tools = [{ type: 'custom', name: 'apply_patch', description: 'Apply patch text' }];

  const first = await postJson(url, {
    model: 'codex-model',
    stream: true,
    input: 'Update the file',
    tools,
  });
  assert.match(first, /response\.custom_tool_call_input\.done/);
  assert.match(first, /"call_id":"call_patch_1"/);
  assert.match(first, /"input":"patch body"/);
  assert.match(first, /data: \[DONE\]/);
  const previousResponseId = first.match(/"id":"(resp_[^"]+)"/)?.[1];
  assert.ok(previousResponseId);

  const second = await postJson(url, {
    model: 'codex-model',
    stream: false,
    previous_response_id: previousResponseId,
    input: [{ type: 'custom_tool_call_output', call_id: 'call_patch_1', output: 'Done' }],
    tools,
  });
  const secondBody = JSON.parse(second);
  assert.equal(secondBody.object, 'response');
  assert.equal(secondBody.status, 'completed');
  assert.equal(secondBody.output[0].content[0].text, 'RESPONSES_TOOL_LOOP_OK');
  assert.equal(upstreamRequests.length, 2);
  assert.equal(upstreamRequests[1].conversationId, upstreamRequests[0].conversationId);
  assert.equal(
    upstreamRequests[1].messages.find((message) => message.role === 'tool').tool_call_name,
    'apply_patch',
  );
});

test('gateway relays Codex namespace tools and ignores hosted web search', async (t) => {
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    const chunk = {
      id: 'chatcmpl-namespace-1',
      toolCalls: [{
        id: 'call_terminal_1',
        type: 'function',
        function: {
          name: 'codex_app__read_thread_terminal',
          arguments: '{}',
        },
      }],
      choices: [{ finishReason: 'tool_calls' }],
      lastOne: true,
      statusCode: 0,
    };
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify(chunk)}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createTestGateway(upstream, null);
  await listen(gateway);
  t.after(() => gateway.close());
  const body = await postJson(
    `http://127.0.0.1:${gateway.address().port}/v1/responses`,
    {
      model: 'gpt-5.5',
      stream: true,
      input: 'Read the terminal',
      tools: [
        {
          type: 'namespace',
          name: 'codex_app',
          description: 'Codex desktop tools',
          tools: [{
            type: 'function',
            name: 'read_thread_terminal',
            description: 'Read the terminal',
            strict: false,
            parameters: { type: 'object', properties: {} },
          }],
        },
        { type: 'web_search', external_web_access: true },
      ],
    },
  );

  assert.match(body, /"type":"function_call"/);
  assert.match(body, /"name":"read_thread_terminal"/);
  assert.match(body, /"namespace":"codex_app"/);
  assert.match(body, /data: \[DONE\]/);
});

test('gateway exposes an OpenAI-compatible model list', async (t) => {
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    model: 'glm-5.2',
    upstreamUrl: 'http://127.0.0.1:1/unused',
    nativeAgent: true,
    forceStream: true,
    encrypt: false,
    extraHeaders: {},
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gateway.address().port}/v1/models`);
  const body = await response.json();
  assert.equal(body.object, 'list');
  assert.equal(body.data[0].object, 'model');
  assert.equal(body.data[0].id, 'glm-5.2');
});

test('gateway protects public API endpoints with the configured bearer key', async (t) => {
  const gateway = createGatewayServer({
    listenHost: '0.0.0.0',
    inboundApiKey: 'new-api-channel-secret',
    model: 'glm-5.2',
    upstreamUrl: 'http://127.0.0.1:1/unused',
    nativeAgent: true,
    forceStream: true,
    encrypt: false,
    extraHeaders: {},
  });
  await listen(gateway);
  t.after(() => gateway.close());
  const url = `http://127.0.0.1:${gateway.address().port}/v1/models`;

  const rejected = await fetch(url);
  assert.equal(rejected.status, 401);
  const accepted = await fetch(url, {
    headers: { authorization: 'Bearer new-api-channel-secret' },
  });
  assert.equal(accepted.status, 200);
});

test('gateway exposes Catpaw request quota through OpenAI billing endpoints', async (t) => {
  let quotaRequests = 0;
  const upstream = http.createServer((req, res) => {
    quotaRequests += 1;
    assert.equal(req.url, '/api/user/limit');
    sendJson(res, {
      code: 0,
      data: {
        modelRequestCount: 17,
        modelRequestLimit: 500,
        modelRemaingCount: 483,
      },
    });
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    inboundApiKey: 'new-api-channel-secret',
    upstreamBaseUrl: baseUrl,
    upstreamUrl: `${baseUrl}/unused`,
    model: 'glm-5.2',
    extraHeaders: { 'Catpaw-Auth': 'quota-token' },
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const origin = `http://127.0.0.1:${gateway.address().port}`;
  const headers = { authorization: 'Bearer new-api-channel-secret' };
  const subscriptionResponse = await fetch(
    `${origin}/v1/dashboard/billing/subscription`,
    { headers },
  );
  const usageResponse = await fetch(
    `${origin}/v1/dashboard/billing/usage?start_date=2026-07-01&end_date=2026-07-12`,
    { headers },
  );
  const subscription = await subscriptionResponse.json();
  const usage = await usageResponse.json();

  assert.equal(subscriptionResponse.status, 200);
  assert.equal(subscription.hard_limit_usd, 500);
  assert.equal(subscription.system_hard_limit_usd, 500);
  assert.equal(usageResponse.status, 200);
  assert.equal(usage.total_usage, 1700);
  assert.equal(quotaRequests, 1);
});

test('gateway uses Catpaw native Agent protocol for a complete Claude tool loop', async (t) => {
  const upstreamRequests = [];
  const upstreamHeaders = [];
  const upstream = http.createServer(async (req, res) => {
    const body = await readJson(req);
    upstreamRequests.push(body);
    upstreamHeaders.push(req.headers);
    const toolResult = body.messages.find((message) => message.role === 'tool');
    const chunk = toolResult
      ? {
          id: 'chatcmpl-2',
          content: 'LOCAL_AGENT_LOOP_OK',
          suggestUuid: 'suggest-2',
          choices: [{ finishReason: 'stop' }],
          lastOne: true,
          statusCode: 0,
        }
      : {
          id: 'chatcmpl-1',
          content: 'Starting inspection.Starting inspection.',
          suggestUuid: 'suggest-1',
          toolCalls: [{
            id: 'call_1',
            type: 'function',
            function: { name: 'TaskList', arguments: '{}' },
          }],
          choices: [{ finishReason: 'tool_calls' }],
          lastOne: true,
          statusCode: 0,
        };

    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify(chunk)}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createGatewayServer({
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    maxSystemChars: 24000,
    maxToolDescriptionChars: 256,
    forceStream: true,
    nativeAgent: true,
    encrypt: false,
    debug: false,
    extraHeaders: {},
    apiKey: '',
    cookie: 'session=local-test',
  });
  await listen(gateway);
  t.after(() => gateway.close());
  const url = `http://127.0.0.1:${gateway.address().port}/v1/messages`;
  const tools = [{ name: 'TaskList', description: 'List tasks', input_schema: { type: 'object' } }];

  const firstResponse = await postJson(url, {
    model: 'claude-fable-5',
    stream: true,
    system: 'Work carefully.',
    messages: [{ role: 'user', content: 'LOCAL_AGENT_TOOL_TEST' }],
    tools,
  });
  assert.match(firstResponse, /"type":"tool_use"/);
  assert.match(firstResponse, /"name":"TaskList"/);
  assert.equal(firstResponse.match(/Starting inspection\./g)?.length, 1);

  const secondResponse = await postJson(url, {
    model: 'claude-fable-5',
    stream: true,
    system: 'Work carefully.',
    messages: [
      { role: 'user', content: 'LOCAL_AGENT_TOOL_TEST' },
      {
        role: 'assistant',
        content: [{ type: 'tool_use', id: 'call_1', name: 'TaskList', input: {} }],
      },
      {
        role: 'user',
        content: [{ type: 'tool_result', tool_use_id: 'call_1', content: 'No tasks' }],
      },
    ],
    tools,
  });
  assert.match(secondResponse, /LOCAL_AGENT_LOOP_OK/);

  assert.equal(upstreamRequests.length, 2);
  assert.equal(upstreamRequests[0].triggerMode, 'AGENT');
  assert.equal(upstreamHeaders[0]['catpaw-cookie'], 'session=local-test');
  assert.equal(upstreamRequests[0].agentModeConfig.type, 'CUSTOM_AGENT');
  const mcpTool = upstreamRequests[0].agentModeConfig.tools.find(
    (tool) => tool.toolUseName === 'use_mcp_tool',
  );
  assert.equal(mcpTool.enable, true);
  assert.equal(
    upstreamRequests[0].agentModeConfig.tools.find(
      (tool) => tool.toolUseName === 'run_terminal_cmd',
    ).enable,
    false,
  );
  assert.equal(upstreamRequests[1].conversationId, upstreamRequests[0].conversationId);
  const toolMessage = upstreamRequests[1].messages.find((message) => message.role === 'tool');
  assert.equal(toolMessage.tool_call_name, 'TaskList');
  assert.equal(toolMessage.suggestUuid, 'suggest-1');
});

test('gateway uses Catpaw native Agent protocol for tool-free auxiliary requests', async (t) => {
  let upstreamRequest;
  const upstream = http.createServer(async (req, res) => {
    upstreamRequest = await readJson(req);
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify({
      id: 'chatcmpl-auxiliary',
      content: 'AUXILIARY_OK',
      choices: [{ delta: { content: 'AUXILIARY_OK' }, finishReason: 'stop' }],
      lastOne: true,
      statusCode: 0,
    })}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createGatewayServer({
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    forceStream: true,
    nativeAgent: true,
    userModelTypeCode: 2,
    encrypt: false,
    debug: false,
    extraHeaders: {},
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(`http://127.0.0.1:${gateway.address().port}/v1/messages`, {
    model: 'claude-fable-5',
    stream: true,
    system: 'Summarize the conversation.',
    messages: [{ role: 'user', content: 'Summarize now.' }],
  });

  assert.match(response, /AUXILIARY_OK/);
  assert.equal(upstreamRequest.triggerMode, 'AGENT');
  assert.equal(upstreamRequest.userModelTypeCode, 2);
  assert.equal(upstreamRequest.agentModeConfig.systemPrompt, 'Summarize the conversation.');
  assert.deepEqual(
    upstreamRequest.agentModeConfig.tools.find((tool) => tool.toolUseName === 'use_mcp_tool'),
    { toolUseName: 'use_mcp_tool', enable: true, mcpTools: [] },
  );
});

test('credential provider selection prefers broker, then SQLite, then manual headers', () => {
  const broker = createCredentialProvider({
    credentialPipe: 'catapi-credential-pipe',
    credentialNonce: 'launch-secret',
    autoRefreshToken: true,
    extraHeaders: { 'Catpaw-Auth': 'legacy-token' },
  });
  const tokenlessBroker = createCredentialProvider({
    credentialPipe: 'catapi-tokenless-pipe',
    credentialNonce: 'tokenless-launch-secret',
    extraHeaders: {},
  });
  const sqlite = createCredentialProvider({
    autoRefreshToken: true,
    extraHeaders: { 'Catpaw-Auth': 'legacy-token' },
  });
  const manual = createCredentialProvider({
    autoRefreshToken: false,
    extraHeaders: { 'Catpaw-Auth': 'manual-token' },
  });

  assert.ok(broker instanceof CredentialBroker);
  assert.ok(tokenlessBroker instanceof CredentialBroker);
  assert.ok(sqlite instanceof CatpawCredentialManager);
  assert.equal(manual, null);
});

test('legacy provider selection reads token and user headers case-insensitively', () => {
  const provider = createCredentialProvider({
    autoRefreshToken: true,
    extraHeaders: {
      'cAtPaW-aUtH': 'mixed-case-token',
      'USER-MIS-ID': 'mixed-case-user',
    },
    cookie: 'passport=mixed-case-token',
  });

  assert.ok(provider instanceof CatpawCredentialManager);
  assert.deepEqual(provider.snapshot(), {
    token: 'mixed-case-token',
    cookie: 'passport=mixed-case-token',
    userMis: 'mixed-case-user',
    generation: 0,
  });
});

test('gateway awaits a broker snapshot before the first upstream attempt', async (t) => {
  let upstreamToken;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    upstreamToken = req.headers['catpaw-auth'];
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"first","content":"BROKER_FIRST_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  let snapshots = 0;
  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => {
      snapshots += 1;
      return credential('broker-token', 'broker-user', 1);
    },
  });
  const gateway = createTestGateway(upstream, credentialProvider);
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(messageUrl(gateway), testMessage('first snapshot'));

  assert.match(response, /BROKER_FIRST_OK/);
  assert.equal(upstreamToken, 'broker-token');
  assert.equal(snapshots, 1);
});

test('broker snapshot atomically replaces mixed-case static credential headers', async (t) => {
  let received;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    received = {
      headers: req.headers,
      counts: countRawHeaders(req.rawHeaders),
    };
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"atomic","content":"ATOMIC_HEADERS_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  const config = loadConfig({
    CATPAW_BASE_URL: baseUrl,
    CATPAW_UPSTREAM_URL: `${baseUrl}/api/gpt/openai/stream`,
    CATPAW_API_KEY: 'current-api-key',
    CATPAW_HEADERS: JSON.stringify({
      'catpaw-auth': 'stale-token',
      COOKIE: 'stale-cookie',
      'cAtPaW-cOoKiE': 'stale-catpaw-cookie',
      'USER-MIS-ID': 'stale-user-mis',
      'User-Uid': 'stale-user-uid',
      'MIS-ID': 'stale-mis-id',
      aUtHoRiZaTiOn: 'Bearer stale-api-key',
    }),
  });
  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => credential('current-token', 'current-user', 5),
  });
  const gateway = createGatewayServer(config, { credentialProvider });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(messageUrl(gateway), testMessage('atomic headers'));

  assert.match(response, /ATOMIC_HEADERS_OK/);
  assert.deepEqual(pickCredentialHeaders(received.headers), {
    authorization: 'Bearer current-api-key',
    'catpaw-auth': 'current-token',
    cookie: 'passport=current-token',
    'catpaw-cookie': 'passport=current-token',
    'user-mis-id': 'current-user',
    'user-uid': 'current-user',
    'mis-id': 'current-user',
  });
  for (const name of CREDENTIAL_HEADER_NAMES) {
    assert.equal(received.counts[name], 1, name);
  }
});

test('manual mode preserves mixed-case static credential headers', async (t) => {
  let received;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    received = req.headers;
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"manual","content":"MANUAL_HEADERS_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  const config = loadConfig({
    CATPAW_BASE_URL: baseUrl,
    CATPAW_UPSTREAM_URL: `${baseUrl}/api/gpt/openai/stream`,
    CATPAW_HEADERS: JSON.stringify({
      'cAtPaW-aUtH': 'manual-token',
      COOKIE: 'manual-cookie',
      'CATPAW-COOKIE': 'manual-catpaw-cookie',
      'USER-MIS-ID': 'manual-user',
      'User-Uid': 'manual-uid',
      'MIS-ID': 'manual-mis',
      AUTHORIZATION: 'Bearer manual-api-key',
    }),
  });
  const gateway = createGatewayServer(config);
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(messageUrl(gateway), testMessage('manual headers'));

  assert.match(response, /MANUAL_HEADERS_OK/);
  assert.deepEqual(pickCredentialHeaders(received), {
    authorization: 'Bearer manual-api-key',
    'catpaw-auth': 'manual-token',
    cookie: 'manual-cookie',
    'catpaw-cookie': 'manual-catpaw-cookie',
    'user-mis-id': 'manual-user',
    'user-uid': 'manual-uid',
    'mis-id': 'manual-mis',
  });
});

test('provider snapshot with no cookie suppresses the static cookie fallback', async (t) => {
  let received;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    received = req.headers;
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"cookieless","content":"COOKIELESS_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => ({
      token: 'current-token',
      cookie: '',
      userMis: 'current-user',
      generation: 6,
    }),
  });
  const gateway = createTestGateway(upstream, credentialProvider, {
    cookie: 'stale-static-cookie',
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(messageUrl(gateway), testMessage('no cookie'));

  assert.match(response, /COOKIELESS_OK/);
  assert.equal(received.cookie, undefined);
  assert.equal(received['catpaw-cookie'], undefined);
});

test('gateway rebuilds encrypted headers and body for one broker replay', async (t) => {
  const attempts = [];
  const upstream = http.createServer(async (req, res) => {
    const encryptedBody = await readText(req);
    attempts.push({
      token: req.headers['catpaw-auth'],
      encryptedKey: req.headers['encrypted-key'],
      encryptedBody,
    });
    if (attempts.length === 1) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end('{"status":401}');
      return;
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"replay","content":"BROKER_REPLAY_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  let current = credential('old-broker-token', 'old-user', 1);
  let snapshots = 0;
  let refreshes = 0;
  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => {
      snapshots += 1;
      return { ...current };
    },
    refreshAfterUnauthorized: async (usedToken) => {
      refreshes += 1;
      assert.equal(usedToken, 'old-broker-token');
      current = credential('new-broker-token', 'new-user', 2);
      return true;
    },
  });
  const gateway = createTestGateway(upstream, credentialProvider, { encrypt: true });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(messageUrl(gateway), testMessage('encrypted replay'));

  assert.match(response, /BROKER_REPLAY_OK/);
  assert.equal(attempts.length, 2);
  assert.deepEqual(attempts.map(({ token }) => token), ['old-broker-token', 'new-broker-token']);
  assert.notEqual(attempts[0].encryptedKey, attempts[1].encryptedKey);
  assert.notEqual(attempts[0].encryptedBody, attempts[1].encryptedBody);
  assert.equal(snapshots, 2);
  assert.equal(refreshes, 1);
});

test('concurrent same-token 401s share one broker refresh operation', async (t) => {
  const rejected = [];
  let attempts = 0;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    attempts += 1;
    if (req.headers['catpaw-auth'] === 'shared-old-token') {
      rejected.push(res);
      if (rejected.length === 2) {
        for (const pending of rejected) {
          pending.writeHead(401, { 'content-type': 'application/json' });
          pending.end('{"status":401}');
        }
      }
      return;
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"shared","content":"SHARED_REFRESH_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  let current = credential('shared-old-token', 'broker-user', 1);
  let refreshOperation;
  let refreshOperations = 0;
  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => ({ ...current }),
    refreshAfterUnauthorized: () => {
      if (!refreshOperation) {
        refreshOperations += 1;
        refreshOperation = Promise.resolve().then(() => {
          current = credential('shared-new-token', 'broker-user', 2);
          return true;
        });
      }
      return refreshOperation;
    },
  });
  const gateway = createTestGateway(upstream, credentialProvider);
  await listen(gateway);
  t.after(() => gateway.close());

  const responses = await Promise.all([
    postJson(messageUrl(gateway), testMessage('concurrent one')),
    postJson(messageUrl(gateway), testMessage('concurrent two')),
  ]);

  assert.equal(responses.every((body) => body.includes('SHARED_REFRESH_OK')), true);
  assert.equal(attempts, 4);
  assert.equal(refreshOperations, 1);
});

test('gateway refreshes Catpaw credentials and replays one unauthorized request', async (t) => {
  const attempts = [];
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    attempts.push({
      token: req.headers['catpaw-auth'],
      cookie: req.headers['catpaw-cookie'],
      userMis: req.headers['user-mis-id'],
    });
    if (attempts.length === 1) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end('{"data":{"message":"auth failed"},"status":401}');
      return;
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify({
      id: 'chatcmpl-refreshed',
      content: 'TOKEN_REFRESH_OK',
      choices: [{ finishReason: 'stop' }],
      lastOne: true,
      statusCode: 0,
    })}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const credentialManager = new CatpawCredentialManager({
    token: 'old-token',
    cookie: 'passport=old-token; sso=old-token',
    userMis: 'old-user',
    refreshAttempts: 1,
    readSession: async () => ({ token: 'new-token', userMis: 'new-user' }),
  });
  const gateway = createGatewayServer({
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    forceStream: true,
    nativeAgent: true,
    userModelTypeCode: 2,
    encrypt: false,
    debug: false,
    extraHeaders: { 'Catpaw-Auth': 'old-token', 'user-mis-id': 'old-user' },
    cookie: 'passport=old-token; sso=old-token',
  }, { credentialManager });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(`http://127.0.0.1:${gateway.address().port}/v1/messages`, {
    model: 'claude-fable-5',
    stream: true,
    messages: [{ role: 'user', content: 'continue the task' }],
  });

  assert.match(response, /TOKEN_REFRESH_OK/);
  assert.deepEqual(attempts, [
    { token: 'old-token', cookie: 'passport=old-token; sso=old-token', userMis: 'old-user' },
    { token: 'new-token', cookie: 'passport=new-token; sso=new-token', userMis: 'new-user' },
  ]);
});

test('gateway maps an unresolved Catpaw 401 to a temporary 503 without replaying', async (t) => {
  let attempts = 0;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    attempts += 1;
    res.writeHead(401, { 'content-type': 'application/json' });
    res.end('{"data":{"message":"auth failed"},"status":401}');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const credentialManager = new CatpawCredentialManager({
    token: 'same-token',
    refreshAttempts: 1,
    readSession: async () => ({ token: 'same-token', userMis: 'user-1' }),
  });
  const gateway = createGatewayServer({
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    forceStream: true,
    nativeAgent: true,
    userModelTypeCode: 2,
    encrypt: false,
    debug: false,
    extraHeaders: { 'Catpaw-Auth': 'same-token' },
  }, { credentialManager });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gateway.address().port}/v1/messages`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      model: 'claude-fable-5',
      stream: true,
      messages: [{ role: 'user', content: 'continue the task' }],
    }),
  });
  const body = await response.json();

  assert.equal(response.status, 503);
  assert.equal(body.error.type, 'upstream_auth_refresh_pending');
  assert.equal(attempts, 1);
});

test('gateway maps a thrown provider refresh error to a sanitized temporary 503', async (t) => {
  await assertRefreshFailureMapsToTemporary503(t, {
    refreshAfterUnauthorized: () => {
      throw new Error('throwing-provider-secret');
    },
    secret: 'throwing-provider-secret',
  });
});

test('gateway maps a provider refresh timeout to a sanitized temporary 503', async (t) => {
  const timeout = new Error('timeout-provider-secret');
  timeout.code = 'CREDENTIAL_BROKER_TIMEOUT';
  await assertRefreshFailureMapsToTemporary503(t, {
    refreshAfterUnauthorized: () => Promise.reject(timeout),
    secret: 'timeout-provider-secret',
  });
});

test('gateway starts and stops the selected credential provider', async () => {
  let starts = 0;
  let stops = 0;
  const credentialProvider = createAsyncCredentialProvider({
    start: () => { starts += 1; },
    stop: () => { stops += 1; },
  });
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamUrl: 'http://127.0.0.1:1/unused',
    model: 'glm-5.2',
  }, { credentialProvider });

  assert.equal(starts, 1);
  await listen(gateway);
  await new Promise((resolve) => gateway.close(resolve));
  assert.equal(stops, 1);
});

test('gateway accepts a credential provider without lifecycle hooks', async (t) => {
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end('data: {"id":"optional-hooks","content":"OPTIONAL_HOOKS_OK","choices":[{"finishReason":"stop"}],"lastOne":true,"statusCode":0}\n\n');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const credentialProvider = {
    snapshot: async () => credential('hookless-token', 'hookless-user', 1),
    refreshAfterUnauthorized: async () => false,
  };
  const gateway = createTestGateway(upstream, credentialProvider);
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(messageUrl(gateway), testMessage('optional hooks'));

  assert.match(response, /OPTIONAL_HOOKS_OK/);
});

test('gateway injects Claude Desktop workspace mounts and rewrites native file tool paths', async (t) => {
  const claudeSessionRoot = await createClaudeSessionRoot(t);
  const upstreamRequests = [];
  const upstream = http.createServer(async (req, res) => {
    const body = await readJson(req);
    upstreamRequests.push(body);

    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify({
      id: 'chatcmpl-1',
      content: '',
      toolCalls: [{
        id: 'call_1',
        type: 'function',
        function: {
          name: 'Write',
          arguments: '{"file_path":"E:\\\\test1\\\\cc-test.txt","content":"ok"}',
        },
      }],
      choices: [{ finishReason: 'tool_calls' }],
      lastOne: true,
      statusCode: 0,
    })}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createGatewayServer({
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    maxSystemChars: 24000,
    maxToolDescriptionChars: 256,
    forceStream: true,
    nativeAgent: true,
    encrypt: false,
    debug: false,
    extraHeaders: {},
    apiKey: '',
    cookie: '',
    claudeSessionRoot,
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await postJson(`http://127.0.0.1:${gateway.address().port}/v1/messages`, {
    model: 'claude-fable-5',
    stream: true,
    system: 'Work carefully.',
    messages: [{ role: 'user', content: 'write a test file' }],
    tools: [{ name: 'Write', description: 'Write a file', input_schema: { type: 'object' } }],
  });

  assert.match(
    upstreamRequests[0].agentModeConfig.systemPrompt,
    /E:\\test1 => \/sessions\/session-one\/mnt\/test1/,
  );
  assert.match(response, /"partial_json":"{\\"file_path\\":\\"\/sessions\/session-one\/mnt\/test1\/cc-test.txt/);
  assert.doesNotMatch(response, /E:\\\\test1/);
});

test('gateway exposes sanitized local status', async (t) => {
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    listenPort: 3000,
    upstreamUrl: 'http://127.0.0.1:1/unused',
    model: 'glm-5.2',
    extraHeaders: { 'Catpaw-Auth': 'secret-token' },
    cookie: 'passport=secret-cookie',
    credentialPipe: 'secret-pipe',
    credentialNonce: 'secret-nonce',
    resourceLimits: { maxAgentSessions: 128 },
  }, { credentialProvider: createAsyncCredentialProvider() });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gateway.address().port}/admin/status`);
  const status = await response.json();

  assert.equal(response.status, 200);
  assert.equal(status.ok, true);
  assert.equal(status.model, 'glm-5.2');
  assert.equal(status.sessions.maximum, 128);
  assert.equal(typeof status.memory.rssBytes, 'number');
  assert.equal(JSON.stringify(status).includes('secret-token'), false);
  assert.equal(JSON.stringify(status).includes('secret-cookie'), false);
  assert.equal(JSON.stringify(status).includes('secret-pipe'), false);
  assert.equal(JSON.stringify(status).includes('secret-nonce'), false);
});

test('gateway rejects request bodies over the configured limit', async (t) => {
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamUrl: 'http://127.0.0.1:1/unused',
    model: 'glm-5.2',
    resourceLimits: { maxRequestBytes: 32 },
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gateway.address().port}/v1/messages`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ messages: [{ role: 'user', content: 'x'.repeat(100) }] }),
  });

  assert.equal(response.status, 413);
});

test('gateway status reads Catpaw quota from the user limit endpoint', async (t) => {
  let quotaToken;
  const upstream = http.createServer((req, res) => {
    assert.equal(req.url, '/api/user/limit');
    quotaToken = req.headers['catpaw-auth'];
    sendJson(res, {
      code: 0,
      data: {
        modelRequestCount: 117,
        modelRequestLimit: 500,
        modelRemaingCount: 383,
      },
    });
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  let snapshots = 0;
  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => {
      snapshots += 1;
      return credential('broker-quota-token', 'quota-user', 3);
    },
  });
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamBaseUrl: baseUrl,
    upstreamUrl: `${baseUrl}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    extraHeaders: { 'Catpaw-Auth': 'stale-static-token' },
  }, { credentialProvider });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gateway.address().port}/admin/status`);
  const status = await response.json();

  assert.deepEqual(status.quota, { remaining: 383, used: 117, total: 500 });
  assert.equal(quotaToken, 'broker-quota-token');
  assert.equal(snapshots, 1);
});

test('gateway automatically resets Catpaw quota below four credits once', async (t) => {
  let limitRequests = 0;
  let resetRequests = 0;
  const upstream = http.createServer(async (req, res) => {
    if (req.url === '/api/user/limit') {
      limitRequests += 1;
      sendJson(res, {
        code: 0,
        data: {
          modelRequestCount: 497,
          modelRequestLimit: 500,
          modelRemaingCount: 3,
        },
      });
      return;
    }
    assert.equal(req.url, '/api/user/addQuota');
    assert.equal(req.method, 'POST');
    assert.deepEqual(await readJson(req), {});
    resetRequests += 1;
    sendJson(res, {
      code: 0,
      data: {
        modelRequestCount: 497,
        modelRequestLimit: 1000,
        modelRemaingCount: 503,
      },
    });
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamBaseUrl: baseUrl,
    upstreamUrl: `${baseUrl}/unused`,
    model: 'glm-5.2',
    autoResetQuota: true,
    extraHeaders: { 'Catpaw-Auth': 'quota-token' },
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const statusUrl = `http://127.0.0.1:${gateway.address().port}/admin/status`;
  const first = await (await fetch(statusUrl)).json();
  const second = await (await fetch(statusUrl)).json();

  assert.deepEqual(first.quota, { remaining: 503, used: 497, total: 1000 });
  assert.equal(first.quotaAutoReset.enabled, true);
  assert.equal(first.quotaAutoReset.threshold, 4);
  assert.equal(typeof first.quotaAutoReset.lastSuccessAt, 'string');
  assert.equal(second.quota.remaining, 503);
  assert.equal(limitRequests, 1);
  assert.equal(resetRequests, 1);
});

test('gateway does not reset Catpaw quota at four credits', async (t) => {
  let resetRequests = 0;
  const upstream = http.createServer((req, res) => {
    if (req.url === '/api/user/addQuota') resetRequests += 1;
    sendJson(res, {
      code: 0,
      data: {
        modelRequestCount: 496,
        modelRequestLimit: 500,
        modelRemaingCount: 4,
      },
    });
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamBaseUrl: baseUrl,
    upstreamUrl: `${baseUrl}/unused`,
    model: 'glm-5.2',
    autoResetQuota: true,
    extraHeaders: { 'Catpaw-Auth': 'quota-token' },
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const status = await (await fetch(
    `http://127.0.0.1:${gateway.address().port}/admin/status`,
  )).json();
  assert.equal(status.quota.remaining, 4);
  assert.equal(resetRequests, 0);
});

test('concurrent status callers await one in-flight quota refresh', async (t) => {
  let quotaRequests = 0;
  const upstream = http.createServer((req, res) => {
    quotaRequests += 1;
    sendJson(res, {
      code: 0,
      data: {
        modelRequestCount: 20,
        modelRequestLimit: 100,
        modelRemaingCount: 80,
      },
    });
  });
  await listen(upstream);
  t.after(() => upstream.close());

  let releaseSnapshot;
  let markSnapshotStarted;
  let snapshots = 0;
  const snapshotStarted = new Promise((resolve) => { markSnapshotStarted = resolve; });
  const heldSnapshot = new Promise((resolve) => { releaseSnapshot = resolve; });
  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => {
      snapshots += 1;
      markSnapshotStarted();
      return heldSnapshot;
    },
  });
  const baseUrl = `http://127.0.0.1:${upstream.address().port}`;
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamBaseUrl: baseUrl,
    upstreamUrl: `${baseUrl}/unused`,
    model: 'glm-5.2',
  }, { credentialProvider });
  await listen(gateway);
  t.after(() => gateway.close());

  const statusUrl = `http://127.0.0.1:${gateway.address().port}/admin/status`;
  const first = fetch(statusUrl);
  await snapshotStarted;
  let secondSettled = false;
  const second = fetch(statusUrl).then((response) => {
    secondSettled = true;
    return response;
  });
  await delay(30);
  const settledBeforeRelease = secondSettled;
  releaseSnapshot(credential('quota-token', 'quota-user', 1));

  const responses = await Promise.all([first, second]);
  const statuses = await Promise.all(responses.map((response) => response.json()));

  assert.equal(settledBeforeRelease, false);
  assert.equal(responses.every((response) => response.status === 200), true);
  assert.deepEqual(statuses.map(({ quota }) => quota), [
    { remaining: 80, used: 20, total: 100 },
    { remaining: 80, used: 20, total: 100 },
  ]);
  assert.equal(snapshots, 1);
  assert.equal(quotaRequests, 1);
});

test('gateway status returns persisted usage for an inclusive date range', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'catapi-status-usage-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const usageStore = new UsageStore(join(directory, 'usage.json'));
  await usageStore.record(
    { inputTokens: 1_500_000, outputTokens: 250_000 },
    new Date(2026, 6, 1, 12),
  );
  await usageStore.record(
    { inputTokens: 2_500_000, outputTokens: 750_000 },
    new Date(2026, 6, 7, 12),
  );
  await usageStore.record(
    { inputTokens: 9_000_000, outputTokens: 9_000_000 },
    new Date(2026, 6, 8, 12),
  );

  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamUrl: 'http://127.0.0.1:1/unused',
    model: 'glm-5.2',
    usageStore,
  });
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(
    `http://127.0.0.1:${gateway.address().port}/admin/status?start=2026-07-01&end=2026-07-07`,
  );
  const status = await response.json();

  assert.equal(response.status, 200);
  assert.deepEqual(status.usage, {
    inputTokens: 4_000_000,
    outputTokens: 1_000_000,
    requests: 2,
    start: '2026-07-01',
    end: '2026-07-07',
  });
});

test('gateway status rejects invalid usage date ranges', async (t) => {
  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamUrl: 'http://127.0.0.1:1/unused',
    model: 'glm-5.2',
    usageStore: new UsageStore(null),
  });
  await listen(gateway);
  t.after(() => gateway.close());

  for (const query of [
    'start=2026-02-30&end=2026-03-01',
    'start=2026-03-02&end=2026-03-01',
    'start=2024-01-01&end=2026-07-10',
    'start=2026-07-01',
  ]) {
    const response = await fetch(
      `http://127.0.0.1:${gateway.address().port}/admin/status?${query}`,
    );
    assert.equal(response.status, 400, query);
  }
});

test('gateway persists streamed token usage after the request completes', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'catapi-captured-usage-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const usageStorePath = join(directory, 'usage.json');
  const upstream = http.createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify({
      id: 'chatcmpl-usage',
      usage: { prompt_tokens: 1200, completion_tokens: 300 },
      choices: [{ delta: { content: 'OK' }, finish_reason: 'stop' }],
    })}\n\n`);
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const gateway = createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/stream`,
    model: 'glm-5.2',
    forceStream: true,
    nativeAgent: false,
    encrypt: false,
    debug: false,
    extraHeaders: {},
    usageStorePath,
  });
  await listen(gateway);
  t.after(() => gateway.close());

  await postJson(`http://127.0.0.1:${gateway.address().port}/v1/messages`, {
    model: 'glm-5.2',
    stream: true,
    messages: [{ role: 'user', content: 'local usage test' }],
  });

  const today = new Date();
  const date = [
    today.getFullYear(),
    String(today.getMonth() + 1).padStart(2, '0'),
    String(today.getDate()).padStart(2, '0'),
  ].join('-');
  const reloaded = new UsageStore(usageStorePath);
  assert.deepEqual(await reloaded.sumRange(date, date), {
    inputTokens: 1200,
    outputTokens: 300,
    requests: 1,
  });
});

function listen(server) {
  return new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
}

function createAsyncCredentialProvider(overrides = {}) {
  return {
    snapshot: async () => credential('default-token', 'default-user', 0),
    refreshAfterUnauthorized: async () => false,
    start: () => {},
    stop: () => {},
    ...overrides,
  };
}

function credential(token, userMis, generation) {
  return {
    token,
    userMis,
    cookie: `passport=${token}`,
    generation,
  };
}

const CREDENTIAL_HEADER_NAMES = [
  'authorization',
  'catpaw-auth',
  'cookie',
  'catpaw-cookie',
  'user-mis-id',
  'user-uid',
  'mis-id',
];

function pickCredentialHeaders(headers) {
  return Object.fromEntries(
    CREDENTIAL_HEADER_NAMES.map((name) => [name, headers[name]]),
  );
}

function countRawHeaders(rawHeaders) {
  const counts = {};
  for (let index = 0; index < rawHeaders.length; index += 2) {
    const name = rawHeaders[index].toLowerCase();
    counts[name] = (counts[name] || 0) + 1;
  }
  return counts;
}

function createTestGateway(upstream, credentialProvider, overrides = {}) {
  return createGatewayServer({
    listenHost: '127.0.0.1',
    upstreamBaseUrl: `http://127.0.0.1:${upstream.address().port}`,
    upstreamUrl: `http://127.0.0.1:${upstream.address().port}/api/gpt/openai/stream`,
    model: 'glm-5.2',
    forceStream: true,
    nativeAgent: true,
    userModelTypeCode: 2,
    encrypt: false,
    debug: false,
    extraHeaders: {},
    ...overrides,
  }, { credentialProvider });
}

function messageUrl(gateway) {
  return `http://127.0.0.1:${gateway.address().port}/v1/messages`;
}

function testMessage(content) {
  return {
    model: 'claude-fable-5',
    stream: true,
    messages: [{ role: 'user', content }],
  };
}

async function assertRefreshFailureMapsToTemporary503(t, {
  refreshAfterUnauthorized,
  secret,
}) {
  let attempts = 0;
  const upstream = http.createServer(async (req, res) => {
    await readJson(req);
    attempts += 1;
    res.writeHead(401, { 'content-type': 'application/json' });
    res.end('{"message":"upstream-auth-body"}');
  });
  await listen(upstream);
  t.after(() => upstream.close());

  const credentialProvider = createAsyncCredentialProvider({
    snapshot: async () => credential('rejected-token', 'refresh-user', 1),
    refreshAfterUnauthorized,
  });
  const gateway = createTestGateway(upstream, credentialProvider);
  await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(messageUrl(gateway), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(testMessage('refresh failure')),
  });
  const body = await response.json();

  assert.equal(response.status, 503);
  assert.equal(body.error.type, 'upstream_auth_refresh_pending');
  assert.match(body.error.message, /upstream-auth-body/);
  assert.equal(JSON.stringify(body).includes(secret), false);
  assert.equal(attempts, 1);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function postJson(url, body) {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  assert.equal(response.status, 200);
  return response.text();
}

async function readText(req) {
  const chunks = [];
  for await (const chunk of req) {
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

async function readJson(req) {
  const chunks = [];
  for await (const chunk of req) {
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function sendJson(res, body) {
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
}

async function createClaudeSessionRoot(t) {
  const root = await mkdtemp(join(tmpdir(), 'catapi-claude-sessions-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const sessionDir = join(root, 'account', '00000000');
  await mkdir(sessionDir, { recursive: true });
  await writeFile(join(sessionDir, 'local_0.json'), JSON.stringify({
    sessionId: 'local-one',
    cliSessionId: 'cli-one',
    processName: 'session-one',
    userSelectedFolders: ['E:\\test1'],
    lastActivityAt: Date.now(),
  }));
  return root;
}

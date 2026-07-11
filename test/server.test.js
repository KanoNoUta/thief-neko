import http from 'node:http';
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createGatewayServer, createCredentialProvider } from '../src/server.js';
import { CatpawCredentialManager } from '../src/catpawCredentials.js';
import { CredentialBroker } from '../src/credentialBroker.js';
import { UsageStore } from '../src/usageStore.js';

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

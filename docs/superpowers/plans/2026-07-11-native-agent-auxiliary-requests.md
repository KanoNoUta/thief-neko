# Native Agent Auxiliary Requests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure tool-free Claude Desktop auxiliary requests use Catpaw's native Agent protocol whenever Native Agent mode is enabled.

**Architecture:** Keep protocol selection inside `handleMessages`. Native Agent mode becomes the single protocol switch, while the existing bounded session store and request/response adapters handle both tool-bearing and tool-free requests. Plain OpenAI behavior remains available only when Native Agent mode is disabled.

**Tech Stack:** Node.js 24, native `node:test`, local HTTP integration tests

---

### Task 1: Reproduce tool-free auxiliary request routing

**Files:**
- Modify: `test/server.test.js`
- Test: `test/server.test.js`

- [ ] **Step 1: Write the failing integration test**

Add a test that starts a local upstream server, captures its JSON body, and sends `/v1/messages` without `tools` to a gateway configured with `nativeAgent: true`:

```js
test('gateway uses Catpaw native Agent protocol for tool-free auxiliary requests', async (t) => {
  let upstreamRequest;
  const upstream = http.createServer(async (req, res) => {
    upstreamRequest = await readJson(req);
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify({
      id: 'chatcmpl-auxiliary',
      content: 'AUXILIARY_OK',
      choices: [{ finishReason: 'stop' }],
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
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
node --test --test-name-pattern "tool-free auxiliary" test/server.test.js
```

Expected: FAIL because the captured request has no `triggerMode` or `agentModeConfig`.

### Task 2: Route every Native Agent request through the Agent adapter

**Files:**
- Modify: `src/server.js:124-137`
- Test: `test/server.test.js`

- [ ] **Step 1: Implement the minimal protocol-selection change**

Replace the tool-dependent condition with the configured protocol flag:

```js
const useNativeAgent = config.nativeAgent;
const agentSession = useNativeAgent ? agentSessions.get(openAIRequest) : null;
```

Keep the existing `buildCatpawAgentRequest` call, session options, plain OpenAI fallback, and streaming normalization unchanged.

- [ ] **Step 2: Run the focused test and verify GREEN**

Run:

```powershell
node --test --test-name-pattern "tool-free auxiliary" test/server.test.js
```

Expected: PASS with one matching test and no failures.

- [ ] **Step 3: Run protocol regression tests**

Run:

```powershell
node --test test/server.test.js test/catpawAgent.test.js
```

Expected: all server and Catpaw Agent tests pass, including the existing complete Claude tool loop.

- [ ] **Step 4: Run the full suite**

Run:

```powershell
npm test
```

Expected: all tests pass with zero failures.

- [ ] **Step 5: Check the final diff**

Run:

```powershell
git diff --check
git diff -- src/server.js test/server.test.js
```

Expected: no whitespace errors and only the protocol condition plus regression test are changed.

- [ ] **Step 6: Commit the fix**

```powershell
git add src/server.js test/server.test.js docs/superpowers/plans/2026-07-11-native-agent-auxiliary-requests.md
git commit -m "fix: route auxiliary requests through native agent"
```

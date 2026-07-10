# Catpaw Token Hot Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refresh Catpaw credentials without restarting the gateway and prevent transient upstream 401 responses from signing Claude Desktop out.

**Architecture:** Add a focused runtime credential manager with polling and single-flight refresh. Inject its snapshots into every upstream header build, retry one pre-stream 401 with rebuilt encryption, and map unresolved Catpaw authentication failures to a temporary 503. The existing controller toggle enables or disables this behavior.

**Tech Stack:** Node.js 24, `node:test`, Windows WPF/.NET 10, PowerShell release packaging

---

### Task 1: Runtime credential state

**Files:**
- Create: `src/catpawCredentials.js`
- Create: `test/catpawCredentials.test.js`

- [ ] Write tests for snapshot replacement, stale Cookie token replacement, preserving credentials on read failure, and one shared refresh read for concurrent 401 callers.
- [ ] Run `node --test test/catpawCredentials.test.js` and confirm failure because the module does not exist.
- [ ] Implement `CatpawCredentialManager` with `snapshot()`, `poll()`, `refreshAfterUnauthorized(usedToken)`, `start()`, and `stop()`.
- [ ] Run the focused test and confirm all credential tests pass.

The manager API used by the server is:

```js
const manager = new CatpawCredentialManager({
  token,
  cookie,
  userMis,
  readSession,
  pollIntervalMs: 5_000,
});

const credential = manager.snapshot();
const changed = await manager.refreshAfterUnauthorized(credential.token);
```

### Task 2: Transparent 401 replay

**Files:**
- Modify: `src/server.js`
- Modify: `test/server.test.js`

- [ ] Add an integration test whose upstream rejects the initial token with 401 and accepts the refreshed token; assert Claude receives one successful stream and upstream receives exactly two requests.
- [ ] Add an integration test whose credential reader returns the same token; assert Claude receives 503, not 401, and upstream receives one request.
- [ ] Run the two tests and confirm they fail before implementation.
- [ ] Create the manager only when automatic refresh is enabled, apply snapshots in `buildUpstreamHeaders`, and rebuild headers plus encrypted body for the single retry.
- [ ] Pass the manager to quota refresh so status polling also uses current credentials.
- [ ] Stop the manager on server close.
- [ ] Run server and credential tests until green.

The retry boundary is:

```js
const first = await sendAttempt();
if (first.response.status === 401 && credentialManager) {
  const changed = await credentialManager.refreshAfterUnauthorized(first.token);
  if (changed) {
    await first.response.body?.cancel();
    return (await sendAttempt()).response;
  }
}
return first.response;
```

Unresolved 401 responses are relayed with status 503 and an `upstream_auth_refresh_pending` error type.

### Task 3: Configuration and controller wiring

**Files:**
- Modify: `src/config.js`
- Modify: `test/config.test.js`
- Modify: `controller/CatapiController/MainWindow.xaml.cs`
- Modify: `start-gateway.ps1`

- [ ] Add a failing config test for `CATPAW_AUTO_REFRESH_TOKEN` defaulting off and accepting `1`.
- [ ] Add `autoRefreshToken` to `loadConfig`.
- [ ] Set `CATPAW_AUTO_REFRESH_TOKEN` from the controller's existing `AutoToken` setting.
- [ ] Enable it in `start-gateway.ps1`, which already resolves Catpaw local state automatically.
- [ ] Run Node and controller tests.

### Task 4: Release verification

**Files:**
- Modify: `package.json`
- Modify: `controller/CatapiController/CatapiController.csproj`
- Modify: `controller/release.ps1`
- Modify: `README.md`

- [ ] Run `npm test` and the controller test project.
- [ ] Set all release references to `0.1.3` and document runtime Token refresh.
- [ ] Build `Thief-Neko-v0.1.3-win-x64.zip`.
- [ ] Verify the executable version, package version, required archive entries, and SHA-256.
- [ ] Commit locally, merge to `main`, push `main`, tag `v0.1.3`, and create the GitHub Release with the ZIP asset.

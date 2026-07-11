# Headless Catpaw Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Thief Neko import, create, refresh, and broker Catpaw login sessions without running the Catpaw desktop application.

**Architecture:** The WPF controller owns DPAPI-protected access/refresh tokens and all login HTTP operations. A current-user-only named pipe gives the Node child short-lived credential snapshots and a coalesced refresh command; legacy manual and SQLite providers remain available when the controller is absent. QR, mobile/SMS, invitation-code, and Yoda flows all feed the same `AuthSessionStore`.

**Tech Stack:** .NET 10 WPF, `HttpClient`, Windows DPAPI, `NamedPipeServerStream`, WebView2, Node.js 24 ESM, `node:test`.

---

## File Map

- Create `controller/CatapiController/AuthSession.cs`: immutable session and redacted status records.
- Create `controller/CatapiController/AuthSessionStore.cs`: DPAPI persistence and legacy migration.
- Create `controller/CatapiController/CatpawAuthClient.cs`: clean-room login HTTP protocol.
- Create `controller/CatapiController/CatpawAuthService.cs`: import/login/refresh orchestration and refresh coalescing.
- Create `controller/CatapiController/CredentialPipeServer.cs`: authenticated current-user named-pipe broker.
- Create `controller/CatapiController/LoginWindow.xaml`: QR and mobile login modal.
- Create `controller/CatapiController/LoginWindow.xaml.cs`: modal state machine and browser fallback.
- Create `controller/CatapiController/YodaChallengeWindow.xaml`: transient WebView2 challenge surface.
- Create `controller/CatapiController/YodaChallengeWindow.xaml.cs`: official Yoda challenge bridge.
- Create `src/credentialBroker.js`: Node named-pipe client and credential provider.
- Modify `src/catpawCredentials.js`: provider-based snapshots and unauthorized refresh.
- Modify `src/config.js`: broker pipe/nonce configuration.
- Modify `src/server.js`: select broker, SQLite, or manual provider.
- Modify `controller/CatapiController/SettingsStore.cs`: auth mode and legacy settings compatibility.
- Modify `controller/CatapiController/MainWindow.xaml`: login status and login/import controls.
- Modify `controller/CatapiController/MainWindow.xaml.cs`: service lifecycle and broker wiring.
- Modify `controller/CatapiController/CatapiController.csproj`: WebView2 dependency.
- Modify `controller/CatapiController.Tests/Program.cs`: controller test registration.
- Create `controller/CatapiController.Tests/AuthTestSupport.cs`: fake HTTP and assertion helpers.
- Create `controller/CatapiController.Tests/AuthSessionStoreTests.cs`: persistence/migration tests.
- Create `controller/CatapiController.Tests/CatpawAuthClientTests.cs`: protocol tests.
- Create `controller/CatapiController.Tests/CatpawAuthServiceTests.cs`: refresh/login orchestration tests.
- Create `controller/CatapiController.Tests/CredentialPipeServerTests.cs`: broker authorization tests.
- Create `test/credentialBroker.test.js`: Node broker tests.
- Modify `test/catpawCredentials.test.js` and `test/server.test.js`: provider and replay coverage.

### Task 1: DPAPI Session Store and Settings Migration

**Files:**
- Create: `controller/CatapiController/AuthSession.cs`
- Create: `controller/CatapiController/AuthSessionStore.cs`
- Create: `controller/CatapiController.Tests/AuthSessionStoreTests.cs`
- Create: `controller/CatapiController.Tests/AuthTestSupport.cs`
- Modify: `controller/CatapiController.Tests/Program.cs`
- Modify: `controller/CatapiController/SettingsStore.cs`

- [ ] **Step 1: Register failing persistence and migration tests**

Add test registrations that save an `AuthSession`, assert no plaintext token is present, load it back, and migrate the existing `ProtectedToken` into an access-only session:

```csharp
internal static class AuthSessionStoreTests
{
    public static IEnumerable<(string, Func<Task>)> All()
    {
        yield return ("auth session round-trips without plaintext", RoundTripAsync);
        yield return ("legacy access token migrates without refresh token", MigrateLegacyAsync);
        yield return ("failed replacement preserves prior session", AtomicWriteAsync);
    }
}
```

- [ ] **Step 2: Run controller tests and verify RED**

Run: `dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj`

Expected: build fails because `AuthSessionStore` and `AuthSession` do not exist.

- [ ] **Step 3: Add the session model and DPAPI store**

Implement these public shapes and keep serialization private:

```csharp
internal sealed record AuthSession(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string AccountLabel,
    string Tenant,
    DateTimeOffset? AccessExpiresAt,
    DateTimeOffset? RefreshExpiresAt,
    DateTimeOffset RefreshedAt);

internal sealed record AuthStatus(bool SignedIn, string AccountLabel, string State);

internal sealed class AuthSessionStore
{
    public Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default);
    public Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default);
    public Task ClearAsync(CancellationToken cancellationToken = default);
}
```

Protect one UTF-8 JSON payload with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`, write `<path>.tmp`, then call `File.Move(temp, path, true)`. Never override a valid file when serialization, protection, or temporary writing fails.

- [ ] **Step 4: Extend controller settings without breaking old JSON**

Add:

```csharp
internal enum AuthenticationMode { Manual, FollowDesktop, Headless }
```

Map the old `AutoToken=true` to `FollowDesktop`, `false` to `Manual`, and default new successful imports/logins to `Headless`. Preserve `Token`, `Tenant`, and `GatewayPath` during migration.

- [ ] **Step 5: Run controller tests and verify GREEN**

Run: `dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj`

Expected: all existing settings tests and the three new store tests print `PASS`.

- [ ] **Step 6: Commit**

```powershell
git add controller/CatapiController controller/CatapiController.Tests
git commit -m "feat: persist headless Catpaw sessions"
```

### Task 2: Clean-Room Catpaw Login Client

**Files:**
- Create: `controller/CatapiController/CatpawAuthClient.cs`
- Create: `controller/CatapiController.Tests/CatpawAuthClientTests.cs`
- Modify: `controller/CatapiController.Tests/Program.cs`

- [ ] **Step 1: Write failing protocol tests with a fake handler**

Cover exact method/path/body/header behavior for:

```text
GET  /api/login/qrcode
POST /api/login/accessToken       {"code":"qr-code"}
POST /api/login/sendSmsVerificationCode {"mobileNo":"138...","uuid":"..."}
POST /api/login/mobile/verify     {"mobileNo":"138...","verificationCode":"123456"}
POST /api/login/mobile            {"mobileNo":"138...","verificationCode":"123456","invitationCode":"ABC123"}
POST /api/login/bindMobile
POST /api/login/refreshToken      {"refreshToken":"refresh-token"}
GET  /api/login/userInfo
```

The fake handler must assert `client-type`, `ide-version`, `tenant`, `platform`, and `Catpaw-Auth` where required. Add tests proving server `code != 0`, malformed JSON, and missing token fields become redacted `CatpawAuthException` messages.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj`

Expected: build fails because `CatpawAuthClient` does not exist.

- [ ] **Step 3: Implement the typed protocol client**

Expose:

```csharp
internal sealed class CatpawAuthClient(HttpClient http, string tenant)
{
    public Task<QrLoginChallenge> CreateQrAsync(CancellationToken ct);
    public Task<QrLoginPoll> PollQrAsync(string code, CancellationToken ct);
    public Task<SmsChallenge> SendSmsAsync(string mobile, string deviceId, CancellationToken ct);
    public Task<MobileVerification> VerifySmsAsync(string mobile, string code, CancellationToken ct);
    public Task<AuthSession> LoginMobileAsync(string mobile, string code, string? invitation, CancellationToken ct);
    public Task<AuthSession> BindMobileAsync(string qrCode, string mobile, string code, string? invitation, CancellationToken ct);
    public Task<AuthSession> RefreshAsync(AuthSession current, CancellationToken ct);
    public Task<AccountInfo> GetUserInfoAsync(string accessToken, CancellationToken ct);
}
```

Use `JsonContent.Create`, `ReadFromJsonAsync`, a shared response envelope parser, and a 15-second HTTP timeout. Do not include response bodies or submitted fields in exceptions.

- [ ] **Step 4: Run tests and verify GREEN**

Run: `dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj`

Expected: every protocol and pre-existing controller test prints `PASS`.

- [ ] **Step 5: Commit**

```powershell
git add controller/CatapiController/CatpawAuthClient.cs controller/CatapiController.Tests
git commit -m "feat: add Catpaw login protocol client"
```

### Task 3: Session Import, Refresh Scheduling, and Coalescing

**Files:**
- Create: `controller/CatapiController/CatpawAuthService.cs`
- Create: `controller/CatapiController.Tests/CatpawAuthServiceTests.cs`
- Modify: `controller/CatapiController.Tests/Program.cs`
- Modify: `src/catpawState.js`
- Modify: `test/catpawState.test.js`

- [ ] **Step 1: Write failing service tests**

Test that two simultaneous `RefreshAsync` calls invoke the client once; successful refresh persists both rotated tokens; network failure retains the last session; authentication rejection marks `LoginRequired`; import reads access token, refresh token, and account ID without printing values.

Define the service boundary:

```csharp
internal sealed class CatpawAuthService
{
    public Task<AuthSession?> GetSessionAsync(CancellationToken ct = default);
    public Task<AuthSession> ImportDesktopSessionAsync(string gatewayPath, CancellationToken ct);
    public Task<AuthSession> RefreshAsync(bool force, CancellationToken ct);
    public Task SaveLoginAsync(AuthSession session, CancellationToken ct);
    public AuthStatus GetStatus();
}
```

- [ ] **Step 2: Run both test suites and verify RED**

Run:

```powershell
npm test -- test/catpawState.test.js
dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj
```

Expected: Node assertion fails because no refresh token is returned; C# build fails because the service is missing.

- [ ] **Step 3: Extend the desktop-state reader safely**

Return this internal shape without logging it:

```js
return {
  token: session.accessToken,
  refreshToken: auth.refreshToken || '',
  userMis: session.account.id,
  accountLabel: session.account.label || '',
};
```

Keep the existing child-process isolation and output only JSON to stdout.

- [ ] **Step 4: Implement refresh coalescing and scheduling**

Use a private `Task<AuthSession>? _refreshTask` guarded by a lock. Refresh five minutes before a supplied expiry; when expiry is missing, refresh every 45 minutes. Use bounded retry delays of 1, 2, and 5 seconds only for transport/5xx failures. Persist before publishing a new in-memory snapshot.

- [ ] **Step 5: Run both suites and verify GREEN**

Run:

```powershell
npm test -- test/catpawState.test.js
dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj
```

Expected: all tests pass and no credential text appears in output.

- [ ] **Step 6: Commit**

```powershell
git add src/catpawState.js test/catpawState.test.js controller/CatapiController controller/CatapiController.Tests
git commit -m "feat: manage Catpaw sessions independently"
```

### Task 4: Authenticated Named-Pipe Credential Broker

**Files:**
- Create: `controller/CatapiController/CredentialPipeServer.cs`
- Create: `controller/CatapiController.Tests/CredentialPipeServerTests.cs`
- Create: `src/credentialBroker.js`
- Create: `test/credentialBroker.test.js`
- Modify: `controller/CatapiController.Tests/Program.cs`

- [ ] **Step 1: Write failing C# pipe tests**

Start a pipe with `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`. Assert the wrong nonce gets `{ "ok": false, "error": "unauthorized" }`, `snapshot` returns access/user fields but no refresh token, and concurrent `refresh` messages share one service refresh.

- [ ] **Step 2: Write failing Node broker tests**

Define the desired API:

```js
const broker = new CredentialBroker({ pipeName, nonce, connect });
assert.deepEqual(await broker.snapshot(), {
  token: 'access', userMis: 'user', cookie: 'passport=access', generation: 3,
});
assert.equal(await broker.refreshAfterUnauthorized('access'), true);
```

Also test timeout, malformed frame, unauthorized response, and secret-free errors.

- [ ] **Step 3: Run tests and verify RED**

Run:

```powershell
npm test -- test/credentialBroker.test.js
dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj
```

Expected: missing broker classes/modules.

- [ ] **Step 4: Implement newline-delimited pipe messages**

Use request frames:

```json
{"nonce":"launch-secret","operation":"snapshot"}
{"nonce":"launch-secret","operation":"refresh","usedToken":"access"}
{"nonce":"launch-secret","operation":"status"}
```

Limit each frame to 16 KiB, process one request per connection, use a two-second client timeout, and clear all byte buffers after parsing where practical. The server response for `snapshot` includes only access token, user ID, cookie derived from access token, and generation.

- [ ] **Step 5: Run tests and verify GREEN**

Run the two commands from Step 3. Expected: both suites pass.

- [ ] **Step 6: Commit**

```powershell
git add controller/CatapiController controller/CatapiController.Tests src/credentialBroker.js test/credentialBroker.test.js
git commit -m "feat: broker Catpaw credentials over named pipes"
```

### Task 5: Gateway Provider Integration and Transparent Replay

**Files:**
- Modify: `src/catpawCredentials.js`
- Modify: `src/config.js`
- Modify: `src/server.js`
- Modify: `test/catpawCredentials.test.js`
- Modify: `test/config.test.js`
- Modify: `test/server.test.js`

- [ ] **Step 1: Add failing provider-selection and replay tests**

Assert `CATPAW_CREDENTIAL_PIPE` plus `CATPAW_CREDENTIAL_NONCE` selects `CredentialBroker`; absent broker variables plus automatic mode selects SQLite; manual mode selects static credentials. Add a server test where the first upstream response is 401, broker refresh changes the token, and exactly one replay succeeds. Add a failure test proving unresolved 401 becomes 503.

- [ ] **Step 2: Run focused Node tests and verify RED**

Run: `npm test -- test/config.test.js test/catpawCredentials.test.js test/server.test.js`

Expected: broker configuration assertions fail.

- [ ] **Step 3: Generalize the credential manager**

Change construction to accept a provider with:

```js
{
  snapshot: () => Promise<CredentialSnapshot> | CredentialSnapshot,
  poll: () => Promise<boolean>,
  refreshAfterUnauthorized: (usedToken) => Promise<boolean>,
}
```

Keep generation monotonic, cookie replacement atomic, the five-second poll for legacy mode, and one replay per rejected request. Never place pipe nonce or tokens in `/admin/status`.

- [ ] **Step 4: Run focused and full Node suites**

Run:

```powershell
npm test -- test/config.test.js test/catpawCredentials.test.js test/server.test.js
npm test
```

Expected: all Node tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src test
git commit -m "feat: use headless credentials in the gateway"
```

### Task 6: QR, Mobile, Invitation, and Yoda Login UI

**Files:**
- Create: `controller/CatapiController/LoginWindow.xaml`
- Create: `controller/CatapiController/LoginWindow.xaml.cs`
- Create: `controller/CatapiController/YodaChallengeWindow.xaml`
- Create: `controller/CatapiController/YodaChallengeWindow.xaml.cs`
- Modify: `controller/CatapiController/CatapiController.csproj`
- Create: `controller/CatapiController.Tests/LoginStateTests.cs`
- Modify: `controller/CatapiController.Tests/Program.cs`

- [ ] **Step 1: Extract and test a UI-independent login state machine**

Add failing tests for `RequestingQr -> WaitingForScan -> Scanned -> SignedIn`, expiry/retry, cancel, `NeedsMobileBinding`, SMS countdown, `NeedsInvitation`, Yoda success retry, and Yoda failure reset. No test may make a live network request.

- [ ] **Step 2: Run controller tests and verify RED**

Run: `dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj`

Expected: missing `LoginStateMachine`.

- [ ] **Step 3: Implement the state machine and modal**

Use a QR/mobile segmented control, a stable 240x240 image area, explicit agreement checkbox, phone input, six-digit code input, 60-second countdown, invitation input only when required, retry/cancel controls, and `Open in browser` using `ProcessStartInfo { UseShellExecute = true }` with the QR image URL.

Polling uses one cancellable loop:

```csharp
while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < challenge.ExpiresAt)
{
    var result = await client.PollQrAsync(challenge.Code, ct);
    if (await ApplyPollResultAsync(result, ct)) return;
    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
}
```

- [ ] **Step 4: Add transient WebView2 Yoda handling**

Add `Microsoft.Web.WebView2` to the controller project. Load only the official Yoda seed script, pass only `requestCode`, and accept only `slideValidationSuccess` or `slideValidationFail` web messages. Dispose the WebView and temporary user-data folder when the challenge closes.

- [ ] **Step 5: Run tests and build the controller**

Run:

```powershell
dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj
dotnet build controller/CatapiController/CatapiController.csproj -c Release
```

Expected: tests pass and Release build succeeds without XAML errors.

- [ ] **Step 6: Commit**

```powershell
git add controller/CatapiController controller/CatapiController.Tests
git commit -m "feat: add headless Catpaw login flows"
```

### Task 7: Main Window and Gateway Lifecycle Integration

**Files:**
- Modify: `controller/CatapiController/MainWindow.xaml`
- Modify: `controller/CatapiController/MainWindow.xaml.cs`
- Modify: `controller/CatapiController/SettingsStore.cs`
- Modify: `controller/CatapiController.Tests/Program.cs`

- [ ] **Step 1: Add failing mode/lifecycle tests**

Test that headless mode starts the pipe before Node, passes only pipe name/nonce and no refresh token, stops the pipe after Node exits, imports desktop credentials only on explicit action, and falls back to manual mode without deleting stored headless credentials.

- [ ] **Step 2: Run controller tests and verify RED**

Run: `dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj`

Expected: lifecycle assertions fail because MainWindow still passes the access token directly.

- [ ] **Step 3: Integrate controls and service startup**

Replace the binary automatic-token toggle with an authentication mode selector: `Headless login`, `Follow Catpaw desktop`, `Manual token`. Add redacted account status, `Login Catpaw`, and `Import existing login`. In headless mode set:

```csharp
info.Environment["CATPAW_CREDENTIAL_PIPE"] = pipe.PipeName;
info.Environment["CATPAW_CREDENTIAL_NONCE"] = pipe.Nonce;
info.Environment.Remove("CATPAW_AUTH_TOKEN");
info.Environment.Remove("CATPAW_COOKIE");
```

Keep existing environment behavior unchanged for desktop-follow and manual modes.

- [ ] **Step 4: Run controller tests and both full builds**

Run:

```powershell
dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj
dotnet publish controller/CatapiController/CatapiController.csproj -c Release -r win-x64 --self-contained true
npm test
```

Expected: all controller and Node tests pass; publish succeeds.

- [ ] **Step 5: Commit**

```powershell
git add controller
git commit -m "feat: integrate headless login with Thief Neko"
```

### Task 8: Documentation, Release Packaging, and Controlled Acceptance

**Files:**
- Modify: `README.md`
- Modify: `package.json`
- Modify: `controller/CatapiController/CatapiController.csproj`
- Modify: `controller/release.ps1`

- [ ] **Step 1: Update user documentation and version**

Document QR login, phone/SMS login, browser fallback, one-time import, how to close Catpaw after migration, recovery when refresh token expires, and the unchanged CCSwitch configuration. Bump the package/controller version consistently.

- [ ] **Step 2: Run secret and packaging checks**

Run:

```powershell
rg -n "accessToken|refreshToken|Catpaw-Auth" logs dist -g '*.log' -g '*.json'
npm test
dotnet run --project controller/CatapiController.Tests/CatapiController.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File .\controller\release.ps1
```

Expected: no credential values in logs/artifacts, all tests pass, and release ZIP is created.

- [ ] **Step 3: Perform controlled live auth validation without model usage**

With the user present: import the current session, confirm quota refresh, close all `CatPawAI.exe` processes, restart Thief Neko, wait past one forced refresh cycle, and confirm `/admin/status` remains healthy. Then validate embedded QR and mobile login using logout only after the imported session has been safely preserved.

- [ ] **Step 4: Perform one user-authorized Claude request**

Send one short Claude Desktop request only after auth-only validation passes. Expected: one response, no Claude sign-out, no duplicate request, and the normal Catpaw quota decrement.

- [ ] **Step 5: Commit release metadata**

```powershell
git add README.md package.json controller
git commit -m "docs: document headless Catpaw login"
```


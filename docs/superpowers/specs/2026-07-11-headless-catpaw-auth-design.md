# Headless Catpaw Authentication Design

## Goal

Allow Thief Neko to authenticate and keep Catpaw credentials current without
running or installing the Catpaw desktop application during normal use.

The same release will provide:

- one-time migration from an existing Catpaw desktop login;
- independent access/refresh token rotation;
- an embedded QR-code login window;
- mobile-number and SMS verification-code login;
- a system-browser QR fallback;
- compatibility with the current manual-token and desktop-state modes.

This does not remove the dependency on the user's Catpaw account, Catpaw cloud
services, or Catpaw quota.

## Clean-Room Protocol Boundary

Thief Neko will implement its own small HTTP client from observed request and
response behavior. It will not load, redistribute, or execute Catpaw's bundled
extensions.

The production login service is `https://catpaw.meituan.com`. Login requests
include the same public client metadata already used by the gateway: client
type, IDE version, tenant, and Windows platform.

The required protocol operations are:

| Operation | Request |
| --- | --- |
| Create QR session | `GET /api/login/qrcode` |
| Poll QR session | `POST /api/login/accessToken` with `{ "code": "..." }` |
| Send SMS code | `POST /api/login/sendSmsVerificationCode` with mobile number and device UUID |
| Check SMS code | `POST /api/login/mobile/verify` |
| Complete mobile login | `POST /api/login/mobile` with the optional invitation code |
| Bind mobile after QR | `POST /api/login/bindMobile` |
| Refresh session | `POST /api/login/refreshToken` with the current refresh token |
| Resolve account | `GET /api/login/userInfo` with `Catpaw-Auth` |

No model request is sent by login, polling, migration, or refresh operations,
so these operations do not consume inference quota.

## Components

### Controller authentication service

`CatpawAuthService` in the WPF controller owns the durable credential state and
all login UI operations. It exposes explicit operations to:

- import the existing Catpaw session;
- start, poll, cancel, and restart a QR login;
- refresh credentials before expiry or on demand;
- return a redacted login status for the UI;
- clear credentials without logging secrets.

Only one login attempt and one refresh operation may run at a time. Concurrent
callers await the existing operation.

### Credential store

The controller stores an `AuthSession` containing:

- access token;
- refresh token;
- access and refresh expiry times when supplied;
- Catpaw account ID and display label;
- tenant and last successful refresh time.

Both tokens are encrypted with Windows DPAPI for the current user. Writes use a
temporary file followed by atomic replacement. Existing settings containing
only an access token remain readable and are migrated after the first import or
login. Tokens, cookies, QR codes, and tool arguments never appear in status
responses or activity logs.

### Controller-to-gateway broker

The controller creates a randomly named Windows named pipe restricted to the
current user before starting the Node gateway. The pipe supports three framed
JSON operations:

- `snapshot`: return the current access token and identity headers;
- `refresh`: coalesce and perform credential refresh, then return a snapshot;
- `status`: return redacted authentication health.

The pipe name and a per-launch nonce are passed to the child process. The nonce
is required on every message. The refresh token stays inside the controller and
is never passed through environment variables, command-line arguments, HTTP
admin endpoints, or logs.

When the controller is not present, the gateway preserves the current manual
token and Catpaw database readers for source/CLI compatibility.

### Gateway credential provider

The existing `CatpawCredentialManager` becomes provider-based. A broker provider
uses the named pipe; the legacy provider continues to read Catpaw's SQLite
state. Before each upstream attempt, the gateway obtains the newest in-memory
snapshot. On a Catpaw `401`, all concurrent requests share one broker refresh
and each rejected request is replayed at most once. If refresh cannot complete,
the gateway returns `503 upstream_auth_refresh_pending` so Claude Desktop does
not sign the user out.

## Login Experience

The credential area gains a redacted account/status row and a `Login Catpaw`
button. The button opens a compact modal consistent with the existing Thief
Neko visual style. A segmented control switches between `QR code` and `Mobile`
without starting overlapping login attempts.

The modal states are:

1. requesting QR code;
2. waiting for agreement and scan;
3. scanned, waiting for confirmation;
4. signed in;
5. expired, with a refresh action;
6. failed, with retry and browser fallback actions.

The QR image returned by Catpaw is displayed directly in the modal. Polling runs
every 500 ms, stops immediately on success/cancel/expiry, and never overlaps.
The user must explicitly accept the Catpaw terms before polling is enabled.

`Open in browser` opens the QR image URL in the system browser while the same
controller polling operation remains active. This is a display fallback rather
than a separate credential flow, so both paths produce an identical session.

If Catpaw reports that the account requires first-time mobile binding, Thief
Neko switches the same modal to the mobile-binding form and preserves the QR
session code until binding succeeds or the user cancels.

### Mobile verification login

The mobile form contains the phone number, six-digit SMS code, terms acceptance,
and a `Send code` action with a 60-second countdown. Thief Neko creates and
persists a non-secret installation UUID for the SMS request. Phone numbers and
verification codes are held only for the active login attempt and are never
written to settings or logs.

Before completing login, Thief Neko calls the verification endpoint. If Catpaw
requires an invitation code, the modal displays a separate six-character input
and submits it with the mobile login request. A successful response is stored
through the same credential path as QR login.

When the SMS endpoint returns a Meituan Yoda challenge request code, an embedded
WebView2 challenge panel loads the official Yoda script from
`https://s0.meituan.net/mxx/yoda/yoda.seed.js`. The panel receives only the
challenge request code. On success it retries the SMS request; on failure or
cancel it clears the pending attempt. WebView2 is created only for a challenge
and disposed immediately afterward, so it does not add an idle background
process during normal gateway use.

## Migration and Startup

When Catpaw is installed and a desktop session exists, the UI offers `Import
existing login`. Import reads the access token, refresh token, and account ID
once, saves them in the new credential store, validates them with the user-info
endpoint, and starts independent refresh management. It does not modify or log
the Catpaw database.

On later launches, Catpaw files and processes are not accessed when a valid
headless session exists. Refresh is scheduled before access-token expiry. If the
server omits an expiry, a conservative periodic refresh is used. Failed network
refreshes use bounded backoff while the last usable access token is retained.
Authentication rejection marks the session as requiring login but does not
delete the encrypted refresh token automatically.

## Compatibility

- Headless login becomes the recommended automatic mode.
- Existing automatic desktop-state mode remains available as `Follow Catpaw
  desktop login`.
- Manual access-token mode remains available.
- Existing `settings.json` files are migrated without losing the saved token,
  tenant, gateway path, or usage history.
- The local Anthropic API and CCSwitch configuration do not change.

## Verification

Automated tests cover protocol parsing, QR state transitions, cancellation,
expiry, mobile validation, SMS countdown state, invitation-code branching,
Yoda challenge transitions, DPAPI store migration, atomic writes, refresh
coalescing, named-pipe authorization, broker fallback, one-replay behavior, and
secret redaction.

Controlled integration verification uses the user's existing session without
printing credentials. QR validation is performed only after the refresh and
migration path is stable. The final acceptance check closes every Catpaw desktop
process, restarts Thief Neko, confirms quota/status access, and performs one
explicitly authorized model request through Claude Desktop.

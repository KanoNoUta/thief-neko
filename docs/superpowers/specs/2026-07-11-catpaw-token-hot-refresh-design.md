# Catpaw Token Hot Refresh Design

## Goal

Keep Claude Desktop sessions alive when Catpaw rotates its local access token while the gateway is running.

## Design

The gateway owns a runtime credential manager when automatic Token mode is enabled. The manager keeps the current Catpaw auth token, user identity headers, and credential cookies in memory. It polls Catpaw's local `state.vscdb` every five seconds and atomically swaps credentials when the token changes.

Every upstream request takes one immutable credential snapshot. If Catpaw returns HTTP 401 before any response is sent to Claude, the gateway asks the manager to refresh. Concurrent 401 responses share one refresh operation. When a new token is available, the gateway rebuilds encrypted headers and body and replays the original request exactly once.

If no new token is available, or the replay also returns 401, the gateway returns HTTP 503 instead of 401. This preserves the error while preventing Claude Desktop from interpreting a transient upstream rotation as rejection of its configured local API key and signing the user out.

## Boundaries

- Manual Token mode never reads Catpaw local state.
- A request is replayed at most once.
- A response is never replayed after streaming output begins.
- Refresh failures preserve the last known credentials.
- Raw tokens and cookies never appear in logs or status responses.
- Closing the gateway stops the polling timer.

## Verification

- Unit tests cover token and Cookie replacement, polling, failed refreshes, and concurrent single-flight refresh.
- HTTP integration tests cover successful 401 refresh/replay and the 503 fallback when credentials do not change.
- Existing Agent, streaming, quota, controller, and configuration tests remain green.


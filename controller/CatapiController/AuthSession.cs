namespace CatapiController;

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

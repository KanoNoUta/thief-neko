namespace CatapiController;

internal sealed record CatpawSession(string Token, string UserMis);

internal sealed record TokenResolution(
    ControllerSettings Settings,
    bool Synced,
    bool UsedFallback);

internal static class TokenResolver
{
    public static TokenResolution Resolve(
        ControllerSettings settings,
        CatpawSession? session)
    {
        if (!settings.AutoToken)
        {
            EnsureUsable(settings.Token);
            return new TokenResolution(settings, Synced: false, UsedFallback: false);
        }

        if (!string.IsNullOrWhiteSpace(session?.Token))
        {
            return new TokenResolution(
                settings with { Token = session.Token.Trim() },
                Synced: true,
                UsedFallback: false);
        }

        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            return new TokenResolution(
                settings,
                Synced: false,
                UsedFallback: true);
        }

        throw new InvalidOperationException("无法读取 Catpaw 登录凭据，且没有可用的手动凭据。");
    }

    private static void EnsureUsable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("请填写有效的 Catpaw 登录凭据。");
        }
    }
}

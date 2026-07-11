using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CatapiController;
using static CatapiController.Tests.AuthTestSupport;

namespace CatapiController.Tests;

internal static class AuthSessionStoreTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("auth session round-trips without plaintext", RoundTripAsync);
        yield return ("legacy access token migrates without refresh token", MigrateLegacyAsync);
        yield return ("failed replacement preserves prior session", AtomicWriteAsync);
        yield return ("clearing removes the saved auth session", ClearAsync);
    }

    private static async Task RoundTripAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        var store = new AuthSessionStore(filePath);
        var session = Session("access-secret", "refresh-secret", "first-account");

        await store.SaveAsync(session);

        var persisted = await File.ReadAllTextAsync(filePath);
        AssertTrue(!persisted.Contains(session.AccessToken, StringComparison.Ordinal),
            "persisted session must not expose the access token");
        AssertTrue(!persisted.Contains(session.RefreshToken, StringComparison.Ordinal),
            "persisted session must not expose the refresh token");
        AssertEqual(session, await store.LoadAsync(), "auth session should round-trip");
    }

    private static async Task MigrateLegacyAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var sessionPath = Path.Combine(directory.Path, "auth-session.json");
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        const string accessToken = "legacy-access-secret";
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(accessToken),
            null,
            DataProtectionScope.CurrentUser);
        var legacy = new
        {
            ProtectedToken = Convert.ToBase64String(protectedBytes),
            Tenant = "legacy-tenant",
            GatewayPath = directory.Path,
            AutoToken = true,
        };
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(legacy));

        var migrated = await new AuthSessionStore(sessionPath, settingsPath).LoadAsync();

        AssertTrue(migrated is not null, "legacy access token should migrate");
        AssertEqual(accessToken, migrated!.AccessToken, "legacy access token should be retained");
        AssertEqual(string.Empty, migrated.RefreshToken, "legacy migration must not invent a refresh token");
        AssertEqual("legacy-tenant", migrated.Tenant, "legacy tenant should be retained");
        AssertTrue(File.Exists(sessionPath), "legacy session should be persisted in the new store");
        File.Delete(settingsPath);
        AssertEqual(migrated, await new AuthSessionStore(sessionPath, settingsPath).LoadAsync(),
            "migrated session should no longer depend on legacy settings");
    }

    private static async Task AtomicWriteAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        var store = new AuthSessionStore(filePath);
        var original = Session("original-access", "original-refresh", "original-account");
        await store.SaveAsync(original);
        Directory.CreateDirectory(filePath + ".tmp");

        var failed = false;
        try
        {
            await store.SaveAsync(Session("replacement-access", "replacement-refresh", "replacement-account"));
        }
        catch (IOException)
        {
            failed = true;
        }
        catch (UnauthorizedAccessException)
        {
            failed = true;
        }

        AssertTrue(failed, "blocked temporary write should fail");
        AssertEqual(original, await store.LoadAsync(), "failed replacement must preserve the prior session");
    }

    private static async Task ClearAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        var store = new AuthSessionStore(filePath);
        await store.SaveAsync(Session("access", "refresh", "account"));

        await store.ClearAsync();

        AssertTrue(!File.Exists(filePath), "clear should remove the persisted session");
        AssertEqual<AuthSession?>(null, await store.LoadAsync(), "cleared store should load as empty");
    }

    private static AuthSession Session(string accessToken, string refreshToken, string accountLabel) => new(
        accessToken,
        refreshToken,
        "user-123",
        accountLabel,
        "tenant",
        DateTimeOffset.Parse("2026-07-11T08:00:00+00:00"),
        DateTimeOffset.Parse("2026-08-11T08:00:00+00:00"),
        DateTimeOffset.Parse("2026-07-11T07:30:00+00:00"));
}

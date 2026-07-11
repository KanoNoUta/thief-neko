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
        yield return ("corrupt Base64 session is unavailable and preserved", CorruptBase64Async);
        yield return ("undecryptable session is unavailable and preserved", UndecryptablePayloadAsync);
        yield return ("invalid decrypted JSON is unavailable and preserved", InvalidJsonAsync);
        yield return ("legacy access token migrates without refresh token", MigrateLegacyAsync);
        yield return ("clear after migration prevents legacy resurrection", ClearAfterMigrationAsync);
        yield return ("clear removes a leftover encrypted temporary payload", ClearRemovesTemporaryPayloadAsync);
        yield return ("failed replacement preserves prior session", AtomicWriteAsync);
        yield return ("locked destination preserves prior session", LockedDestinationAsync);
        yield return ("clearing removes the saved auth session", ClearAsync);
    }

    private static async Task CorruptBase64Async()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        const string corruptPayload = "not-valid-base64";
        await File.WriteAllTextAsync(filePath, corruptPayload);

        await AssertUnavailableAndPreservedAsync(filePath, corruptPayload);
    }

    private static async Task UndecryptablePayloadAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        var corruptPayload = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);
        await File.WriteAllTextAsync(filePath, corruptPayload);

        await AssertUnavailableAndPreservedAsync(filePath, corruptPayload);
    }

    private static async Task InvalidJsonAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        var invalidJson = Encoding.UTF8.GetBytes("{invalid-json");
        var protectedPayload = ProtectedData.Protect(
            invalidJson,
            null,
            DataProtectionScope.CurrentUser);
        var corruptPayload = Convert.ToBase64String(protectedPayload);
        await File.WriteAllTextAsync(filePath, corruptPayload);

        await AssertUnavailableAndPreservedAsync(filePath, corruptPayload);
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
        await WriteLegacySettingsAsync(settingsPath, directory.Path, accessToken);

        var store = new AuthSessionStore(sessionPath, settingsPath);
        var migrated = await store.LoadAsync();

        AssertTrue(migrated is not null, "legacy access token should migrate");
        AssertEqual(accessToken, migrated!.AccessToken, "legacy access token should be retained");
        AssertEqual(string.Empty, migrated.RefreshToken, "legacy migration must not invent a refresh token");
        AssertEqual("legacy-tenant", migrated.Tenant, "legacy tenant should be retained");
        AssertTrue(File.Exists(sessionPath), "legacy session should be persisted in the new store");
        AssertEqual(migrated, await store.LoadAsync(), "persisted migrated session should round-trip");

        File.Delete(sessionPath);

        AssertEqual<AuthSession?>(null, await store.LoadAsync(),
            "legacy settings should be imported only once");
        AssertTrue(File.Exists(settingsPath), "migration must preserve legacy settings");
    }

    private static async Task ClearAfterMigrationAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var sessionPath = Path.Combine(directory.Path, "auth-session.json");
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        await WriteLegacySettingsAsync(settingsPath, directory.Path, "legacy-clear-secret");
        var store = new AuthSessionStore(sessionPath, settingsPath);
        AssertTrue(await store.LoadAsync() is not null, "legacy session should migrate before clear");

        await store.ClearAsync();

        AssertTrue(!File.Exists(sessionPath), "clear should remove the migrated session");
        AssertTrue(File.Exists(settingsPath), "clear must not remove unrelated legacy settings");
        AssertEqual<AuthSession?>(null, await store.LoadAsync(),
            "clear should prevent legacy token resurrection");
    }

    private static async Task ClearRemovesTemporaryPayloadAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var sessionPath = Path.Combine(directory.Path, "auth-session.json");
        var temporaryPath = sessionPath + ".tmp";
        var store = new AuthSessionStore(sessionPath);
        await store.SaveAsync(Session("temporary-access", "temporary-refresh", "temporary-account"));
        File.Copy(sessionPath, temporaryPath);

        await store.ClearAsync();

        AssertTrue(!File.Exists(sessionPath), "clear should remove the primary payload");
        AssertTrue(!File.Exists(temporaryPath), "clear should remove the temporary payload");
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

    private static async Task LockedDestinationAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "auth-session.json");
        var store = new AuthSessionStore(filePath);
        var original = Session("locked-access", "locked-refresh", "locked-account");
        await store.SaveAsync(original);

        var failed = false;
        using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await store.SaveAsync(Session(
                    "replacement-access",
                    "replacement-refresh",
                    "replacement-account"));
            }
            catch (IOException)
            {
                failed = true;
            }
            catch (UnauthorizedAccessException)
            {
                failed = true;
            }
        }

        AssertTrue(failed, "destination lock should prevent atomic replacement");
        AssertTrue(File.Exists(filePath + ".tmp"),
            "replacement failure should occur after the temporary payload is written");
        AssertEqual(original, await store.LoadAsync(),
            "failed move replacement must preserve the prior session");
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

    private static async Task AssertUnavailableAndPreservedAsync(string filePath, string contents)
    {
        var loaded = await new AuthSessionStore(filePath).LoadAsync();

        AssertEqual<AuthSession?>(null, loaded, "corrupt session should be unavailable");
        AssertEqual(contents, await File.ReadAllTextAsync(filePath),
            "corrupt session file should be preserved unchanged");
    }

    private static async Task WriteLegacySettingsAsync(
        string settingsPath,
        string gatewayPath,
        string accessToken)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(accessToken),
            null,
            DataProtectionScope.CurrentUser);
        var legacy = new
        {
            ProtectedToken = Convert.ToBase64String(protectedBytes),
            Tenant = "legacy-tenant",
            GatewayPath = gatewayPath,
            AutoToken = true,
        };
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(legacy));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CatapiController;
using CatapiController.Tests;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("settings persist automatic Token mode", SettingsPersistAutomaticModeAsync),
    ("legacy settings default automatic Token mode to off", LegacySettingsDefaultToManualAsync),
    ("legacy automatic settings map to follow desktop mode", LegacyAutomaticSettingsMapToFollowDesktopAsync),
    ("settings persist headless authentication mode", SettingsPersistHeadlessModeAsync),
    ("automatic mode selects current Catpaw Token", AutomaticModeSelectsCurrentTokenAsync),
    ("manual mode preserves saved Token", ManualModePreservesSavedTokenAsync),
    ("automatic mode falls back to saved Token", AutomaticModeFallsBackAsync),
    ("automatic mode rejects missing current and fallback Tokens", AutomaticModeRejectsMissingTokenAsync),
};
tests.AddRange(AuthSessionStoreTests.All());
tests.AddRange(CatpawAuthClientTests.All());
tests.AddRange(CatpawAuthServiceTests.All());
tests.AddRange(CredentialPipeServerTests.All());
tests.AddRange(LoginStateTests.All());
tests.AddRange(ControllerIntegrationTests.All());

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static async Task SettingsPersistAutomaticModeAsync()
{
    using var directory = new TemporaryDirectory();
    var filePath = Path.Combine(directory.Path, "settings.json");
    var store = new SettingsStore(filePath);
    var settings = new ControllerSettings("manual-token", "tenant", directory.Path, true);

    await store.SaveAsync(settings);
    var loaded = await store.LoadAsync();

    Equal(settings, loaded, "saved settings should round-trip");
}

static async Task LegacySettingsDefaultToManualAsync()
{
    using var directory = new TemporaryDirectory();
    var filePath = Path.Combine(directory.Path, "settings.json");
    var protectedBytes = ProtectedData.Protect(
        Encoding.UTF8.GetBytes("legacy-token"),
        null,
        DataProtectionScope.CurrentUser);
    var legacy = new
    {
        ProtectedToken = Convert.ToBase64String(protectedBytes),
        Tenant = "tenant",
        GatewayPath = directory.Path,
    };
    await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(legacy));

    var loaded = await new SettingsStore(filePath).LoadAsync();

    True(loaded is not null, "legacy settings should load");
    False(loaded!.AutoToken, "legacy settings should remain in manual mode");
    Equal(AuthenticationMode.Manual, loaded.AuthenticationMode,
        "legacy settings without AutoToken should map to manual mode");
}

static async Task LegacyAutomaticSettingsMapToFollowDesktopAsync()
{
    using var directory = new TemporaryDirectory();
    var filePath = Path.Combine(directory.Path, "settings.json");
    var protectedBytes = ProtectedData.Protect(
        Encoding.UTF8.GetBytes("legacy-token"),
        null,
        DataProtectionScope.CurrentUser);
    var legacy = new
    {
        ProtectedToken = Convert.ToBase64String(protectedBytes),
        Tenant = "tenant",
        GatewayPath = directory.Path,
        AutoToken = true,
    };
    await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(legacy));

    var loaded = await new SettingsStore(filePath).LoadAsync();

    True(loaded is not null, "legacy automatic settings should load");
    Equal(AuthenticationMode.FollowDesktop, loaded!.AuthenticationMode,
        "legacy AutoToken=true should map to follow desktop mode");
    Equal("legacy-token", loaded.Token, "legacy token should be retained");
    Equal("tenant", loaded.Tenant, "legacy tenant should be retained");
    Equal(directory.Path, loaded.GatewayPath, "legacy gateway path should be retained");
}

static async Task SettingsPersistHeadlessModeAsync()
{
    using var directory = new TemporaryDirectory();
    var filePath = Path.Combine(directory.Path, "settings.json");
    var settings = new ControllerSettings(
        "fallback-token",
        "tenant",
        directory.Path,
        AuthenticationMode.Headless);

    var store = new SettingsStore(filePath);
    await store.SaveAsync(settings);

    Equal(settings, await store.LoadAsync(), "headless settings should round-trip");
}

static Task AutomaticModeSelectsCurrentTokenAsync()
{
    var settings = new ControllerSettings("manual-token", "tenant", "gateway", true);
    var result = TokenResolver.Resolve(settings, new CatpawSession("fresh-token", "user"));

    Equal("fresh-token", result.Settings.Token, "automatic mode should use current Token");
    True(result.Synced, "automatic mode should report a successful sync");
    False(result.UsedFallback, "successful sync should not report fallback");
    return Task.CompletedTask;
}

static Task ManualModePreservesSavedTokenAsync()
{
    var settings = new ControllerSettings("manual-token", "tenant", "gateway", false);
    var result = TokenResolver.Resolve(settings, new CatpawSession("fresh-token", "user"));

    Equal("manual-token", result.Settings.Token, "manual mode should preserve saved Token");
    False(result.Synced, "manual mode should not report a sync");
    False(result.UsedFallback, "manual mode should not report fallback");
    return Task.CompletedTask;
}

static Task AutomaticModeFallsBackAsync()
{
    var settings = new ControllerSettings("manual-token", "tenant", "gateway", true);
    var result = TokenResolver.Resolve(settings, null);

    Equal("manual-token", result.Settings.Token, "automatic mode should retain fallback Token");
    False(result.Synced, "fallback should not report a sync");
    True(result.UsedFallback, "missing Catpaw state should report fallback");
    return Task.CompletedTask;
}

static Task AutomaticModeRejectsMissingTokenAsync()
{
    var settings = new ControllerSettings("", "tenant", "gateway", true);
    try
    {
        TokenResolver.Resolve(settings, null);
    }
    catch (InvalidOperationException error)
    {
        False(error.Message.Contains("token", StringComparison.OrdinalIgnoreCase),
            "error must not expose credential content");
        return Task.CompletedTask;
    }

    throw new InvalidOperationException("missing Tokens should fail");
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }
}

static void True(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void False(bool value, string message) => True(!value, message);

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"thief-neko-tests-{Guid.NewGuid():N}");

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, true);
}

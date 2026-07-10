using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CatapiController;

var tests = new (string Name, Func<Task> Run)[]
{
    ("settings persist automatic Token mode", SettingsPersistAutomaticModeAsync),
    ("legacy settings default automatic Token mode to off", LegacySettingsDefaultToManualAsync),
    ("automatic mode selects current Catpaw Token", AutomaticModeSelectsCurrentTokenAsync),
    ("manual mode preserves saved Token", ManualModePreservesSavedTokenAsync),
    ("automatic mode falls back to saved Token", AutomaticModeFallsBackAsync),
    ("automatic mode rejects missing current and fallback Tokens", AutomaticModeRejectsMissingTokenAsync),
};

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

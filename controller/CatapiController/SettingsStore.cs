using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace CatapiController;

internal enum AuthenticationMode
{
    Manual,
    FollowDesktop,
    Headless,
}

internal sealed record ControllerSettings(
    string Token,
    string Tenant,
    string GatewayPath,
    AuthenticationMode AuthenticationMode = AuthenticationMode.Manual)
{
    public ControllerSettings(string Token, string Tenant, string GatewayPath, bool AutoToken)
        : this(
            Token,
            Tenant,
            GatewayPath,
            AutoToken ? AuthenticationMode.FollowDesktop : AuthenticationMode.Manual)
    {
    }

    public bool AutoToken => AuthenticationMode == AuthenticationMode.FollowDesktop;
}

internal sealed class SettingsStore
{
    private sealed record StoredSettings(
        string ProtectedToken,
        string Tenant,
        string GatewayPath,
        bool AutoToken = false,
        AuthenticationMode? AuthenticationMode = null);

    public string FilePath { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Catapi",
            "settings.json");
    }

    public async Task<ControllerSettings?> LoadAsync()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredSettings>(await File.ReadAllTextAsync(FilePath));
        if (stored is null)
        {
            return null;
        }

        var protectedBytes = Convert.FromBase64String(stored.ProtectedToken);
        var tokenBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        var authenticationMode = stored.AuthenticationMode
            ?? (stored.AutoToken ? AuthenticationMode.FollowDesktop : AuthenticationMode.Manual);
        return new ControllerSettings(
            Encoding.UTF8.GetString(tokenBytes),
            stored.Tenant,
            stored.GatewayPath,
            authenticationMode);
    }

    public async Task SaveAsync(ControllerSettings settings)
    {
        if ((settings.AuthenticationMode == AuthenticationMode.Manual && string.IsNullOrWhiteSpace(settings.Token))
            || string.IsNullOrWhiteSpace(settings.Tenant)
            || !Directory.Exists(settings.GatewayPath))
        {
            throw new InvalidOperationException("Token、Tenant 和有效的网关目录不能为空。");
        }

        var tokenBytes = Encoding.UTF8.GetBytes(settings.Token.Trim());
        var protectedBytes = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
        var stored = new StoredSettings(
            Convert.ToBase64String(protectedBytes),
            settings.Tenant.Trim(),
            settings.GatewayPath,
            settings.AutoToken,
            settings.AuthenticationMode);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(stored));
        File.Move(temporaryPath, FilePath, true);
    }
}

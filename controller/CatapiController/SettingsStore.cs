using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace CatapiController;

internal sealed record ControllerSettings(string Token, string Tenant, string GatewayPath);

internal sealed class SettingsStore
{
    private sealed record StoredSettings(string ProtectedToken, string Tenant, string GatewayPath);

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Catapi",
        "settings.json");

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
        return new ControllerSettings(Encoding.UTF8.GetString(tokenBytes), stored.Tenant, stored.GatewayPath);
    }

    public async Task SaveAsync(ControllerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Token)
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
            settings.GatewayPath);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(stored));
        File.Move(temporaryPath, FilePath, true);
    }
}

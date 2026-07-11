using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace CatapiController;

internal sealed class AuthSessionStore
{
    private sealed record LegacyStoredSettings(string ProtectedToken, string Tenant);

    private readonly string? _legacySettingsPath;

    public string FilePath { get; }

    public AuthSessionStore(string? filePath = null, string? legacySettingsPath = null)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Catapi");
        FilePath = filePath ?? Path.Combine(directory, "auth-session.json");
        _legacySettingsPath = legacySettingsPath
            ?? (filePath is null ? Path.Combine(directory, "settings.json") : null);
    }

    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(FilePath))
        {
            return await LoadProtectedSessionAsync(cancellationToken);
        }

        return await MigrateLegacySessionAsync(cancellationToken);
    }

    public async Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[]? protectedPayload = null;
        try
        {
            protectedPayload = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
            var encoded = Convert.ToBase64String(protectedPayload);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, encoded, Encoding.UTF8, cancellationToken);
            File.Move(temporaryPath, FilePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(FilePath);
        return Task.CompletedTask;
    }

    private async Task<AuthSession?> LoadProtectedSessionAsync(CancellationToken cancellationToken)
    {
        var encoded = await File.ReadAllTextAsync(FilePath, cancellationToken);
        var protectedPayload = Convert.FromBase64String(encoded);
        byte[]? payload = null;
        try
        {
            payload = ProtectedData.Unprotect(protectedPayload, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthSession>(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPayload);
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private async Task<AuthSession?> MigrateLegacySessionAsync(CancellationToken cancellationToken)
    {
        if (_legacySettingsPath is null || !File.Exists(_legacySettingsPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_legacySettingsPath, cancellationToken);
        var legacy = JsonSerializer.Deserialize<LegacyStoredSettings>(json);
        if (legacy is null || string.IsNullOrWhiteSpace(legacy.ProtectedToken))
        {
            return null;
        }

        var protectedToken = Convert.FromBase64String(legacy.ProtectedToken);
        byte[]? tokenBytes = null;
        try
        {
            tokenBytes = ProtectedData.Unprotect(protectedToken, null, DataProtectionScope.CurrentUser);
            var session = new AuthSession(
                Encoding.UTF8.GetString(tokenBytes),
                string.Empty,
                string.Empty,
                string.Empty,
                legacy.Tenant,
                null,
                null,
                DateTimeOffset.UtcNow);
            await SaveAsync(session, cancellationToken);
            return session;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedToken);
            if (tokenBytes is not null)
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
    }
}

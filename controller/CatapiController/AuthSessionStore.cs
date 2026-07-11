using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace CatapiController;

internal sealed class AuthSessionStore
{
    private sealed record LegacyStoredSettings(string ProtectedToken, string Tenant);

    private readonly string? _legacySettingsPath;
    private readonly string _migrationMarkerPath;

    public string FilePath { get; }

    public AuthSessionStore(string? filePath = null, string? legacySettingsPath = null)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Catapi");
        FilePath = filePath ?? Path.Combine(directory, "auth-session.json");
        _legacySettingsPath = legacySettingsPath
            ?? (filePath is null ? Path.Combine(directory, "settings.json") : null);
        _migrationMarkerPath = FilePath + ".migration-complete";
    }

    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(FilePath))
        {
            return await LoadProtectedSessionAsync(cancellationToken);
        }

        return File.Exists(_migrationMarkerPath)
            ? null
            : await MigrateLegacySessionAsync(cancellationToken);
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

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(
            _migrationMarkerPath,
            string.Empty,
            Encoding.UTF8,
            cancellationToken);
        File.Delete(FilePath);
        File.Delete(FilePath + ".tmp");
    }

    private async Task<AuthSession?> LoadProtectedSessionAsync(CancellationToken cancellationToken)
    {
        byte[]? protectedPayload = null;
        byte[]? payload = null;
        try
        {
            var encoded = await File.ReadAllTextAsync(FilePath, cancellationToken);
            protectedPayload = Convert.FromBase64String(encoded);
            payload = ProtectedData.Unprotect(protectedPayload, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthSession>(payload);
        }
        catch (Exception error) when (error is FormatException
            or CryptographicException
            or JsonException
            or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

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
            await File.WriteAllTextAsync(
                _migrationMarkerPath,
                string.Empty,
                Encoding.UTF8,
                cancellationToken);
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

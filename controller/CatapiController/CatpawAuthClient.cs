using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CatapiController;

internal sealed record QrLoginChallenge(string Code, string QrCodeUrl, DateTimeOffset? ExpiresAt);

internal sealed record QrLoginPoll(
    string Status,
    AuthSession? Session,
    bool RequiresMobileBinding);

internal sealed record SmsChallenge(string Uuid, string? RequestCode);

internal sealed record MobileVerification(bool Verified, bool InvitationCodeRequired);

internal sealed record AccountInfo(string UserId, string AccountLabel);

internal sealed class CatpawAuthException : Exception
{
    public CatpawAuthException(string message) : base(message)
    {
    }
}

internal sealed class CatpawAuthClient
{
    private static readonly Uri ServiceRoot = new("https://catpaw.meituan.com");
    private readonly HttpClient _http;
    private readonly string _tenant;

    public CatpawAuthClient(HttpClient http, string tenant)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
    }

    public async Task<QrLoginChallenge> CreateQrAsync(CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Get, "/api/login/qrcode", null, null, _tenant,
            "QR challenge", ct);
        var code = RequiredString(data, "QR challenge", "code");
        var qrCodeUrl = RequiredString(data, "QR challenge", "qrcodeUrl", "qrCodeUrl", "url", "qrcode");
        return new QrLoginChallenge(code, qrCodeUrl,
            OptionalDateTime(data, "expiresAt", "expireTime", "expirationTime"));
    }

    public async Task<QrLoginPoll> PollQrAsync(string code, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/login/accessToken",
            new Dictionary<string, object?> { ["code"] = code }, null, _tenant, "QR poll", ct);
        var status = OptionalString(data, "status", "loginStatus") ?? string.Empty;
        var requiresBinding = OptionalBoolean(data,
            "requiresMobileBinding", "needBindMobile", "mobileBindingRequired");

        var hasAccess = HasNonemptyString(data, "accessToken");
        var hasRefresh = HasNonemptyString(data, "refreshToken");
        AuthSession? session = null;
        if (hasAccess || hasRefresh)
        {
            session = ParseSession(data, _tenant, "QR poll");
        }

        return new QrLoginPoll(status, session, requiresBinding);
    }

    public async Task<SmsChallenge> SendSmsAsync(
        string mobile,
        string deviceId,
        CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/login/sendSmsVerificationCode",
            new Dictionary<string, object?>
            {
                ["mobileNo"] = mobile,
                ["uuid"] = deviceId,
            }, null, _tenant, "SMS request", ct);
        return new SmsChallenge(
            RequiredString(data, "SMS request", "uuid"),
            OptionalString(data, "requestCode"));
    }

    public async Task<MobileVerification> VerifySmsAsync(
        string mobile,
        string code,
        CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/login/mobile/verify",
            new Dictionary<string, object?>
            {
                ["mobileNo"] = mobile,
                ["verificationCode"] = code,
            }, null, _tenant, "SMS verification", ct);
        return new MobileVerification(
            OptionalBoolean(data, "verified", "valid", "success"),
            OptionalBoolean(data,
                "invitationCodeRequired", "needInvitationCode", "invitationRequired"));
    }

    public async Task<AuthSession> LoginMobileAsync(
        string mobile,
        string code,
        string? invitation,
        CancellationToken ct)
    {
        var body = MobileBody(mobile, code, invitation);
        var data = await SendAsync(HttpMethod.Post, "/api/login/mobile", body, null, _tenant,
            "mobile login", ct);
        return ParseSession(data, _tenant, "mobile login");
    }

    public async Task<AuthSession> BindMobileAsync(
        string qrCode,
        string mobile,
        string code,
        string? invitation,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["code"] = qrCode };
        foreach (var pair in MobileBody(mobile, code, invitation))
        {
            body.Add(pair.Key, pair.Value);
        }

        var data = await SendAsync(HttpMethod.Post, "/api/login/bindMobile", body, null, _tenant,
            "mobile binding", ct);
        return ParseSession(data, _tenant, "mobile binding");
    }

    public async Task<AuthSession> RefreshAsync(AuthSession current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        var data = await SendAsync(HttpMethod.Post, "/api/login/refreshToken",
            new Dictionary<string, object?> { ["refreshToken"] = current.RefreshToken },
            current.AccessToken, current.Tenant, "token refresh", ct);
        var refreshed = ParseSession(data, current.Tenant, "token refresh");
        return refreshed with
        {
            UserId = OptionalString(data, "userId", "id", "userMis") ?? current.UserId,
            AccountLabel = OptionalString(data, "accountLabel", "accountName", "name", "nickname")
                ?? current.AccountLabel,
        };
    }

    public async Task<AccountInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Get, "/api/login/userInfo", null, accessToken, _tenant,
            "user info", ct);
        return new AccountInfo(
            RequiredString(data, "user info", "userId", "id", "userMis"),
            RequiredString(data, "user info", "accountLabel", "accountName", "name", "nickname"));
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? accessToken,
        string tenant,
        string operation,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, RequestUri(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddHeader(request, "client-type", "CatPaw IDE");
        AddHeader(request, "ide-version", "2026.2.3");
        AddHeader(request, "tenant", tenant);
        AddHeader(request, "platform", "win32-x64");
        if (accessToken is not null)
        {
            AddHeader(request, "Catpaw-Auth", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(operation, $"HTTP status {(int)response.StatusCode}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("code", out var codeElement) ||
                !TryReadInt(codeElement, out var logicalCode))
            {
                throw Failure(operation, "invalid response");
            }

            if (logicalCode != 0)
            {
                throw Failure(operation, $"logical code {logicalCode}");
            }

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw Failure(operation, "missing data");
            }

            return data.Clone();
        }
        catch (JsonException)
        {
            throw Failure(operation, "malformed response");
        }
    }

    private Uri RequestUri(string path) => _http.BaseAddress is null
        ? new Uri(ServiceRoot, path)
        : new Uri(_http.BaseAddress, path);

    private static Dictionary<string, object?> MobileBody(
        string mobile,
        string code,
        string? invitation)
    {
        var body = new Dictionary<string, object?>
        {
            ["mobileNo"] = mobile,
            ["verificationCode"] = code,
        };
        if (!string.IsNullOrWhiteSpace(invitation))
        {
            body["invitationCode"] = invitation;
        }

        return body;
    }

    private static AuthSession ParseSession(JsonElement data, string tenant, string operation)
    {
        var accessToken = RequiredString(data, operation, "accessToken");
        var refreshToken = RequiredString(data, operation, "refreshToken");
        return new AuthSession(
            accessToken,
            refreshToken,
            OptionalString(data, "userId", "id", "userMis") ?? string.Empty,
            OptionalString(data, "accountLabel", "accountName", "name", "nickname") ?? string.Empty,
            tenant,
            OptionalDateTime(data, "accessExpiresAt", "accessTokenExpiresAt", "expiresAt"),
            OptionalDateTime(data, "refreshExpiresAt", "refreshTokenExpiresAt"),
            DateTimeOffset.UtcNow);
    }

    private static string RequiredString(JsonElement data, string operation, params string[] names) =>
        OptionalString(data, names) is { Length: > 0 } value
            ? value
            : throw Failure(operation, "missing required fields");

    private static string? OptionalString(JsonElement data, params string[] names)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return null;
    }

    private static bool HasNonemptyString(JsonElement data, string name) =>
        !string.IsNullOrWhiteSpace(OptionalString(data, name));

    private static bool OptionalBoolean(JsonElement data, params string[] names)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static DateTimeOffset? OptionalDateTime(JsonElement data, params string[] names)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var dateTime))
            {
                return dateTime;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp))
            {
                try
                {
                    return timestamp > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                        : DateTimeOffset.FromUnixTimeSeconds(timestamp);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt32(out result);
        }

        result = default;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out result);
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value) =>
        request.Headers.TryAddWithoutValidation(name, value);

    private static CatpawAuthException Failure(string operation, string reason) =>
        new($"Catpaw authentication {operation} failed ({reason}).");
}

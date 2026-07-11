using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CatapiController;

internal sealed record QrLoginChallenge(string Code, string QrCodeUrl, DateTimeOffset ExpiresAt);

internal sealed record QrLoginPoll(
    string Status,
    AuthSession? Session,
    bool RequiresMobileBinding);

internal sealed record SmsChallenge(string Uuid, string? RequestCode);

internal sealed record MobileVerification(bool Verified, bool InvitationCodeRequired);

internal sealed record AccountInfo(string UserId, string AccountLabel);

internal interface ICatpawAuthClient
{
    Task<AuthSession> RefreshAsync(AuthSession current, CancellationToken ct);
    Task<AccountInfo> GetUserInfoAsync(string accessToken, CancellationToken ct);
}

internal enum CatpawAuthFailureKind
{
    Protocol,
    AuthRejected,
    Transient,
}

internal sealed class CatpawAuthException : Exception
{
    public CatpawAuthException(
        string message,
        CatpawAuthFailureKind kind = CatpawAuthFailureKind.Protocol) : base(message)
    {
        Kind = kind;
    }

    public CatpawAuthFailureKind Kind { get; }
}

internal sealed class CatpawAuthClient : ICatpawAuthClient
{
    private static readonly Uri ServiceRoot = new("https://catpaw.meituan.com");
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;
    private readonly string _tenant;
    private readonly TimeSpan _operationTimeout;

    public CatpawAuthClient(HttpClient http, string tenant)
        : this(http, tenant, DefaultOperationTimeout)
    {
    }

    internal CatpawAuthClient(HttpClient http, string tenant, TimeSpan operationTimeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _operationTimeout = operationTimeout;
    }

    public async Task<QrLoginChallenge> CreateQrAsync(CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Get, "/api/login/qrcode", null, null, _tenant,
            "QR challenge", ct);
        var code = RequiredString(data, "QR challenge", "code");
        var qrCodeUrl = RequiredHttpsUrl(data, "QR challenge",
            "qrcodeUrl", "qrCodeUrl", "url", "qrcode");
        var expiresAt = RequiredDateTime(data, "QR challenge",
            "expiresAt", "expireTime", "expirationTime");
        return new QrLoginChallenge(code, qrCodeUrl, expiresAt);
    }

    public async Task<QrLoginPoll> PollQrAsync(string code, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/login/accessToken",
            new Dictionary<string, object?> { ["code"] = code }, null, _tenant, "QR poll", ct);
        var status = NormalizePollStatus(RequiredString(
            data, "QR poll", "status", "state", "loginStatus"));
        var mobileBound = OptionalStrictBoolean(data, "QR poll", "mobileBound");
        var explicitBinding = OptionalStrictBoolean(data, "QR poll",
            "requiresMobileBinding", "needBindMobile", "mobileBindingRequired");
        var requiresBinding = explicitBinding ?? (mobileBound is false);

        var hasTokenFields = HasAnyProperty(data, "accessToken", "refreshToken");
        var isTerminal = status is "confirmed" or "mobileBound";
        if (hasTokenFields != isTerminal)
        {
            throw Failure("QR poll", "invalid state data");
        }

        AuthSession? session = null;
        if (hasTokenFields)
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
            OptionalString(data, "SMS request", "requestCode"));
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
            RequiredBoolean(data, "SMS verification", "verified", "valid", "success"),
            RequiredBoolean(data, "SMS verification",
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
        var refreshed = ParseSession(data, current.Tenant, "token refresh", requireIdentity: false);
        refreshed = refreshed with
        {
            UserId = OptionalString(data, "token refresh", "userId", "id", "userMis")
                ?? current.UserId,
            AccountLabel = OptionalString(data, "token refresh",
                    "accountLabel", "accountName", "name", "nickname")
                ?? current.AccountLabel,
        };
        if (string.IsNullOrWhiteSpace(refreshed.UserId) ||
            string.IsNullOrWhiteSpace(refreshed.AccountLabel))
        {
            throw Failure("token refresh", "missing identity fields");
        }

        return refreshed;
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
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operationCancellation.CancelAfter(_operationTimeout);
        var operationToken = operationCancellation.Token;

        try
        {
            using var request = new HttpRequestMessage(method, new Uri(ServiceRoot, path));
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
                request, HttpCompletionOption.ResponseHeadersRead, operationToken);
            if (!response.IsSuccessStatusCode)
            {
                var kind = (int)response.StatusCode >= 500
                    ? CatpawAuthFailureKind.Transient
                    : CatpawAuthFailureKind.AuthRejected;
                throw Failure(operation, $"HTTP status {(int)response.StatusCode}", kind);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(operationToken);
                using var document = await JsonDocument.ParseAsync(
                    stream, cancellationToken: operationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("code", out var codeElement) ||
                    !TryReadInt(codeElement, out var logicalCode))
                {
                    throw Failure(operation, "invalid response");
                }

                if (logicalCode != 0)
                {
                    throw Failure(operation, $"logical code {logicalCode}",
                        CatpawAuthFailureKind.AuthRejected);
                }

                if (!root.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object)
                {
                    throw Failure(operation, "invalid data");
                }

                return data.Clone();
            }
            catch (JsonException)
            {
                throw Failure(operation, "malformed response");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            throw Failure(operation, "timed out", CatpawAuthFailureKind.Transient);
        }
    }

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

    private static AuthSession ParseSession(
        JsonElement data,
        string tenant,
        string operation,
        bool requireIdentity = true)
    {
        var accessToken = RequiredString(data, operation, "accessToken");
        var refreshToken = RequiredString(data, operation, "refreshToken");
        var userId = requireIdentity
            ? RequiredString(data, operation, "userId", "id", "userMis")
            : OptionalString(data, operation, "userId", "id", "userMis") ?? string.Empty;
        var accountLabel = requireIdentity
            ? RequiredString(data, operation, "accountLabel", "accountName", "name", "nickname")
            : OptionalString(data, operation,
                "accountLabel", "accountName", "name", "nickname") ?? string.Empty;
        return new AuthSession(
            accessToken,
            refreshToken,
            userId,
            accountLabel,
            tenant,
            OptionalDateTime(data, operation,
                "accessExpiresAt", "accessTokenExpiresAt", "expiresAt"),
            OptionalDateTime(data, operation, "refreshExpiresAt", "refreshTokenExpiresAt"),
            DateTimeOffset.UtcNow);
    }

    private static string RequiredString(JsonElement data, string operation, params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Failure(operation, "invalid required fields");
            }

            return value.GetString()!;
        }

        throw Failure(operation, "missing required fields");
    }

    private static string RequiredHttpsUrl(
        JsonElement data,
        string operation,
        params string[] names)
    {
        var value = RequiredString(data, operation, names);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw Failure(operation, "invalid required fields");
        }

        return value;
    }

    private static DateTimeOffset RequiredDateTime(
        JsonElement data,
        string operation,
        params string[] names) => OptionalDateTime(data, operation, names)
        ?? throw Failure(operation, "invalid required fields");

    private static string? OptionalString(
        JsonElement data,
        string operation,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw Failure(operation, "invalid optional fields");
                }

                return text;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            throw Failure(operation, "invalid optional fields");
        }

        return null;
    }

    private static string NormalizePollStatus(string status)
    {
        if (status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            return "pending";
        }

        if (status.Equals("scanned", StringComparison.OrdinalIgnoreCase))
        {
            return "scanned";
        }

        if (status.Equals("confirmed", StringComparison.OrdinalIgnoreCase))
        {
            return "confirmed";
        }

        if (status.Equals("mobileBound", StringComparison.OrdinalIgnoreCase))
        {
            return "mobileBound";
        }

        throw Failure("QR poll", "unknown state");
    }

    private static bool HasAnyProperty(JsonElement data, params string[] names) =>
        names.Any(name => data.TryGetProperty(name, out _));

    private static bool? OptionalStrictBoolean(
        JsonElement data,
        string operation,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Failure(operation, "invalid boolean fields");
            }

            return value.GetBoolean();
        }

        return null;
    }

    private static bool RequiredBoolean(
        JsonElement data,
        string operation,
        params string[] names) => OptionalStrictBoolean(data, operation, names)
        ?? throw Failure(operation, "missing boolean fields");

    private static DateTimeOffset? OptionalDateTime(
        JsonElement data,
        string operation,
        params string[] names)
    {
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
                    throw Failure(operation, "invalid timestamp fields");
                }
            }

            throw Failure(operation, "invalid timestamp fields");
        }

        return null;
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value) =>
        request.Headers.TryAddWithoutValidation(name, value);

    private static CatpawAuthException Failure(
        string operation,
        string reason,
        CatpawAuthFailureKind kind = CatpawAuthFailureKind.Protocol) =>
        new($"Catpaw authentication {operation} failed ({reason}).", kind);
}

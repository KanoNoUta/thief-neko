using System.Net;
using System.Text;
using System.Text.Json;
using CatapiController;
using static CatapiController.Tests.AuthTestSupport;

namespace CatapiController.Tests;

internal static class CatpawAuthClientTests
{
    private const string Tenant = "catpaw-test-tenant";

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Catpaw auth creates QR challenge with public metadata", CreateQrAsync);
        yield return ("Catpaw auth polls QR challenge with exact request", PollQrAsync);
        yield return ("Catpaw auth sends SMS with exact request", SendSmsAsync);
        yield return ("Catpaw auth verifies SMS with exact request", VerifySmsAsync);
        yield return ("Catpaw auth logs in by mobile with invitation", LoginMobileAsync);
        yield return ("Catpaw auth omits absent mobile invitation", LoginMobileWithoutInvitationAsync);
        yield return ("Catpaw auth binds mobile with QR context", BindMobileAsync);
        yield return ("Catpaw auth refreshes with current credentials", RefreshAsync);
        yield return ("Catpaw auth gets user info with current credentials", GetUserInfoAsync);
        yield return ("Catpaw auth rejects logical errors without secrets", NonzeroCodeIsRedactedAsync);
        yield return ("Catpaw auth rejects HTTP errors without response body", HttpErrorIsRedactedAsync);
        yield return ("Catpaw auth rejects malformed JSON without response body", MalformedJsonIsRedactedAsync);
        yield return ("Catpaw auth rejects missing tokens without submitted secrets", MissingTokenIsRedactedAsync);
        yield return ("Catpaw auth honors caller cancellation", CancellationAsync);
        yield return ("Catpaw auth preserves injected HTTP timeout", PreservesTimeoutAsync);
    }

    private static async Task CreateQrAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Get, "/api/login/qrcode", null, false);
            return JsonResponse("""{"code":0,"data":{"code":"qr-result","qrcodeUrl":"https://example.invalid/qr","expiresAt":"2026-07-11T09:00:00Z"}}""");
        });

        var result = await fixture.Client.CreateQrAsync(default);

        AssertEqual("qr-result", result.Code, "QR code should be parsed");
        AssertEqual("https://example.invalid/qr", result.QrCodeUrl, "QR URL should be parsed");
        AssertEqual(DateTimeOffset.Parse("2026-07-11T09:00:00Z"), result.ExpiresAt,
            "QR expiry should be parsed");
    }

    private static async Task PollQrAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/accessToken",
                """{"code":"qr-code-secret"}""", false);
            return JsonResponse(SessionEnvelope("confirmed"));
        });

        var result = await fixture.Client.PollQrAsync("qr-code-secret", default);

        AssertEqual("confirmed", result.Status, "QR status should be parsed");
        AssertTrue(result.Session is not null, "confirmed QR poll should contain a session");
        AssertEqual("access-result", result.Session!.AccessToken, "QR access token should be parsed");
        AssertTrue(!result.RequiresMobileBinding, "confirmed QR login should not require binding");
    }

    private static async Task SendSmsAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/sendSmsVerificationCode",
                """{"mobileNo":"13800138000","uuid":"device-uuid"}""", false);
            return JsonResponse("""{"code":0,"data":{"uuid":"sms-uuid","requestCode":"yoda-request"}}""");
        });

        var result = await fixture.Client.SendSmsAsync("13800138000", "device-uuid", default);

        AssertEqual("sms-uuid", result.Uuid, "SMS UUID should be parsed");
        AssertEqual("yoda-request", result.RequestCode, "challenge request code should be parsed");
    }

    private static async Task VerifySmsAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/mobile/verify",
                """{"mobileNo":"13800138000","verificationCode":"123456"}""", false);
            return JsonResponse("""{"code":0,"data":{"verified":true,"invitationCodeRequired":true}}""");
        });

        var result = await fixture.Client.VerifySmsAsync("13800138000", "123456", default);

        AssertTrue(result.Verified, "verification result should be parsed");
        AssertTrue(result.InvitationCodeRequired, "invitation requirement should be parsed");
    }

    private static async Task LoginMobileAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/mobile",
                """{"mobileNo":"13800138000","verificationCode":"123456","invitationCode":"INVITE"}""",
                false);
            return JsonResponse(SessionEnvelope());
        });

        var result = await fixture.Client.LoginMobileAsync("13800138000", "123456", "INVITE", default);

        AssertSession(result);
    }

    private static async Task LoginMobileWithoutInvitationAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/mobile",
                """{"mobileNo":"13800138000","verificationCode":"123456"}""", false);
            return JsonResponse(SessionEnvelope());
        });

        await fixture.Client.LoginMobileAsync("13800138000", "123456", null, default);
    }

    private static async Task BindMobileAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/bindMobile",
                """{"code":"qr-code-secret","mobileNo":"13800138000","verificationCode":"123456","invitationCode":"INVITE"}""",
                false);
            return JsonResponse(SessionEnvelope());
        });

        var result = await fixture.Client.BindMobileAsync(
            "qr-code-secret", "13800138000", "123456", "INVITE", default);

        AssertSession(result);
    }

    private static async Task RefreshAsync()
    {
        var current = CurrentSession();
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/refreshToken",
                """{"refreshToken":"current-refresh-secret"}""", true, current.AccessToken, current.Tenant);
            return JsonResponse("""{"code":0,"data":{"accessToken":"rotated-access","refreshToken":"rotated-refresh","accessExpiresAt":"2026-07-11T10:00:00Z","refreshExpiresAt":"2026-08-11T10:00:00Z"}}""");
        });

        var result = await fixture.Client.RefreshAsync(current, default);

        AssertEqual("rotated-access", result.AccessToken, "refresh should rotate access token");
        AssertEqual("rotated-refresh", result.RefreshToken, "refresh should rotate refresh token");
        AssertEqual(current.UserId, result.UserId, "refresh should preserve user ID");
        AssertEqual(current.AccountLabel, result.AccountLabel, "refresh should preserve account label");
        AssertEqual(current.Tenant, result.Tenant, "refresh should preserve tenant");
    }

    private static async Task GetUserInfoAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Get, "/api/login/userInfo", null, true,
                "access-request-secret");
            return JsonResponse("""{"code":0,"data":{"userId":"user-result","accountLabel":"Catpaw User"}}""");
        });

        var result = await fixture.Client.GetUserInfoAsync("access-request-secret", default);

        AssertEqual("user-result", result.UserId, "user ID should be parsed");
        AssertEqual("Catpaw User", result.AccountLabel, "account label should be parsed");
    }

    private static async Task NonzeroCodeIsRedactedAsync()
    {
        const string bodySecret = "server-body-secret";
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new
            {
                code = 40123,
                message = bodySecret,
                data = new { accessToken = "body-access-secret" },
            }))));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.LoginMobileAsync(
            "13800138000", "123456", "INVITE", default));

        AssertRedacted(error, bodySecret, "body-access-secret", "13800138000", "123456", "INVITE");
    }

    private static async Task HttpErrorIsRedactedAsync()
    {
        const string bodySecret = "http-body-secret";
        using var fixture = Fixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(bodySecret, Encoding.UTF8, "application/json"),
        }));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.PollQrAsync("qr-code-secret", default));

        AssertRedacted(error, bodySecret, "qr-code-secret");
    }

    private static async Task MalformedJsonIsRedactedAsync()
    {
        const string bodySecret = "malformed-body-secret";
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse("{" + bodySecret)));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.SendSmsAsync(
            "13800138000", "device-uuid", default));

        AssertRedacted(error, bodySecret, "13800138000", "device-uuid");
    }

    private static async Task MissingTokenIsRedactedAsync()
    {
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(
            """{"code":0,"data":{"accessToken":"only-access-secret"}}""")));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.BindMobileAsync(
            "qr-code-secret", "13800138000", "123456", "INVITE", default));

        AssertRedacted(error, "only-access-secret", "qr-code-secret", "13800138000", "123456", "INVITE");
    }

    private static async Task CancellationAsync()
    {
        using var fixture = Fixture(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await fixture.Client.CreateQrAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("caller cancellation should propagate");
    }

    private static Task PreservesTimeoutAsync()
    {
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse("""{"code":0,"data":{}}""")),
            TimeSpan.FromSeconds(47));

        AssertEqual(TimeSpan.FromSeconds(47), fixture.Http.Timeout,
            "client construction must preserve the injected timeout");
        return Task.CompletedTask;
    }

    private static async Task AssertRequestAsync(
        HttpRequestMessage request,
        HttpMethod method,
        string path,
        string? body,
        bool authenticated,
        string accessToken = "access-request-secret",
        string tenant = Tenant)
    {
        AssertEqual(method, request.Method, "HTTP method should match");
        AssertEqual(path, request.RequestUri!.AbsolutePath, "request path should match");
        AssertHeader(request, "client-type", "CatPaw IDE");
        AssertHeader(request, "ide-version", "2026.2.3");
        AssertHeader(request, "tenant", tenant);
        AssertHeader(request, "platform", "win32-x64");
        AssertTrue(request.Headers.Accept.Any(value => value.MediaType == "application/json"),
            "request should accept application/json");

        if (authenticated)
        {
            AssertHeader(request, "Catpaw-Auth", accessToken);
        }
        else
        {
            AssertTrue(!request.Headers.Contains("Catpaw-Auth"),
                "unauthenticated request must not contain Catpaw-Auth");
        }

        if (body is null)
        {
            AssertTrue(request.Content is null, "GET request should not have a body");
            return;
        }

        AssertEqual("application/json", request.Content!.Headers.ContentType!.MediaType,
            "POST content type should be application/json");
        AssertJsonEqual(body, await request.Content.ReadAsStringAsync());
    }

    private static void AssertHeader(HttpRequestMessage request, string name, string expected)
    {
        AssertTrue(request.Headers.TryGetValues(name, out var values), $"missing {name} header");
        AssertEqual(expected, values!.Single(), $"{name} header should match");
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(actual);
        AssertTrue(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement),
            $"JSON body should match: actual {actual}");
    }

    private static void AssertSession(AuthSession session)
    {
        AssertEqual("access-result", session.AccessToken, "access token should be parsed");
        AssertEqual("refresh-result", session.RefreshToken, "refresh token should be parsed");
        AssertEqual("user-result", session.UserId, "user ID should be parsed");
        AssertEqual("Catpaw User", session.AccountLabel, "account label should be parsed");
        AssertEqual(Tenant, session.Tenant, "client tenant should be assigned");
        AssertEqual(DateTimeOffset.Parse("2026-07-11T09:00:00Z"), session.AccessExpiresAt,
            "access expiry should be parsed");
        AssertEqual(DateTimeOffset.Parse("2026-08-11T09:00:00Z"), session.RefreshExpiresAt,
            "refresh expiry should be parsed");
    }

    private static async Task<CatpawAuthException> CaptureAuthErrorAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (CatpawAuthException error)
        {
            return error;
        }

        throw new InvalidOperationException("operation should throw CatpawAuthException");
    }

    private static void AssertRedacted(Exception error, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            AssertTrue(!error.ToString().Contains(secret, StringComparison.Ordinal),
                "authentication exception must not expose secrets");
        }
    }

    private static AuthSession CurrentSession() => new(
        "current-access-secret",
        "current-refresh-secret",
        "current-user",
        "Current User",
        "current-tenant",
        DateTimeOffset.Parse("2026-07-11T08:00:00Z"),
        DateTimeOffset.Parse("2026-08-11T08:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T07:30:00Z"));

    private static string SessionEnvelope(string? status = null) => JsonSerializer.Serialize(new
    {
        code = 0,
        data = new
        {
            status,
            accessToken = "access-result",
            refreshToken = "refresh-result",
            userId = "user-result",
            accountLabel = "Catpaw User",
            accessExpiresAt = "2026-07-11T09:00:00Z",
            refreshExpiresAt = "2026-08-11T09:00:00Z",
        },
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static ClientFixture Fixture(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response,
        TimeSpan? timeout = null) => Fixture((request, _) => response(request), timeout);

    private static ClientFixture Fixture(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response,
        TimeSpan? timeout = null)
    {
        var http = new HttpClient(new DelegateHandler(response))
        {
            BaseAddress = new Uri("https://catpaw.meituan.com"),
            Timeout = timeout ?? TimeSpan.FromSeconds(31),
        };
        return new ClientFixture(http, new CatpawAuthClient(http, Tenant));
    }

    private sealed record ClientFixture(HttpClient Http, CatpawAuthClient Client) : IDisposable
    {
        public void Dispose() => Http.Dispose();
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}

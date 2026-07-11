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
        yield return ("Catpaw auth decrypts live protocol response format", DecryptProtocolResponseAsync);
        yield return ("Catpaw auth polls QR challenge with exact request", PollQrAsync);
        yield return ("Catpaw auth sends SMS with exact request", SendSmsAsync);
        yield return ("Catpaw auth verifies SMS with exact request", VerifySmsAsync);
        yield return ("Catpaw auth logs in by mobile with invitation", LoginMobileAsync);
        yield return ("Catpaw auth omits absent mobile invitation", LoginMobileWithoutInvitationAsync);
        yield return ("Catpaw auth binds mobile with QR context", BindMobileAsync);
        yield return ("Catpaw auth refreshes with current credentials", RefreshAsync);
        yield return ("Catpaw auth gets user info with current credentials", GetUserInfoAsync);
        yield return ("Catpaw auth pins requests to the official origin", PinsOfficialOriginAsync);
        yield return ("Catpaw auth defaults operation timeout to fifteen seconds", DefaultTimeoutAsync);
        yield return ("Catpaw auth enforces its operation timeout", InternalTimeoutAsync);
        yield return ("Catpaw auth rejects logical errors without secrets", NonzeroCodeIsRedactedAsync);
        yield return ("Catpaw auth rejects HTTP errors without response body", HttpErrorIsRedactedAsync);
        yield return ("Catpaw auth rejects malformed JSON without response body", MalformedJsonIsRedactedAsync);
        yield return ("Catpaw auth rejects invalid envelope schemas", InvalidEnvelopeSchemaAsync);
        yield return ("Catpaw auth rejects missing tokens without submitted secrets", MissingTokenIsRedactedAsync);
        yield return ("Catpaw auth rejects non-object success data", NonObjectDataAsync);
        yield return ("Catpaw auth rejects invalid QR challenge schemas", InvalidQrChallengeSchemasAsync);
        yield return ("Catpaw auth supports strict QR polling states", PollingStatesAsync);
        yield return ("Catpaw auth rejects invalid QR polling schemas", InvalidPollingSchemasAsync);
        yield return ("Catpaw auth rejects invalid SMS schemas", InvalidSmsSchemasAsync);
        yield return ("Catpaw auth rejects invalid verification schemas", InvalidVerificationSchemasAsync);
        yield return ("Catpaw auth rejects blank and numeric tokens", InvalidTokenSchemasAsync);
        yield return ("Catpaw auth rejects invalid session expiry schemas", InvalidSessionExpirySchemaAsync);
        yield return ("Catpaw auth rejects invalid identity schemas", InvalidIdentitySchemasAsync);
        yield return ("Catpaw auth honors caller cancellation", CancellationAsync);
        yield return ("Catpaw auth preserves injected HTTP timeout", PreservesTimeoutAsync);
    }

    private static async Task CreateQrAsync()
    {
        using var fixture = Fixture(async request =>
        {
            await AssertRequestAsync(request, HttpMethod.Get, "/api/login/qrcode", null, false);
            return JsonResponse("""{"code":0,"data":{"code":"qr-result","qrCodeImageUrl":"https://example.invalid/qr","expireTime":1783760400000}}""");
        });

        var result = await fixture.Client.CreateQrAsync(default);

        AssertEqual("qr-result", result.Code, "QR code should be parsed");
        AssertEqual("https://example.invalid/qr", result.QrCodeUrl, "QR URL should be parsed");
        AssertEqual(DateTimeOffset.Parse("2026-07-11T09:00:00Z"), result.ExpiresAt,
            "QR expiry should be parsed");
    }

    private static Task DecryptProtocolResponseAsync()
    {
        const string encryptedKey = "Af/QmmfjVa8nCIcOQ487QyoTU1q+UScLDCWBRIQEoleTJqZsyqzzAfjqo/DKM7AUjjZzgCZcBcV9vKF95tyK6E1qqABQLlASqwrKORmekCZnuE+iBQJzQPm40/mz9DAO3JYCHz0Hb+6QAemcXIOAWFLVpNijQrPI2kywO71JXaM7ic8PULBPCSb2P+R9qegs9rw/muT7z4CtITLAAKnUB3xhMz/UrkLkPe2VvTnlfVx/qZXvw9cPBeF8n33XHtGDSV3J5vvA5QkKivK7wbRXI0sdw/Lu2zjjJXm5V+0Htp2l85HLwF3BVjtX0S7k+xZ/YPKh8iXXezxpzK+Y5aJfwg==";
        const string encryptedBody = "\"BlDvwjbM12mV/egbcOQhDuJohF/HY0nQBMga4Nx2os4P+rJE4+hyoSs6zf1uim3+xSNt2uDlxX7T3FsXU9vlPw==\"";

        var clear = CatpawProtocolCrypto.DecryptResponse(encryptedBody, encryptedKey);
        using var document = JsonDocument.Parse(clear);
        AssertEqual(1000, document.RootElement.GetProperty("code").GetInt32(),
            "encrypted Catpaw response should decrypt before parsing");
        return Task.CompletedTask;
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
        AssertSecretEqual("access-result", result.Session!.AccessToken,
            "QR access token should be parsed");
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

        AssertSecretEqual("rotated-access", result.AccessToken, "refresh should rotate access token");
        AssertSecretEqual("rotated-refresh", result.RefreshToken, "refresh should rotate refresh token");
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
            return JsonResponse("""{"code":0,"data":{"uid":"user-result","loginName":"Catpaw User"}}""");
        });

        var result = await fixture.Client.GetUserInfoAsync("access-request-secret", default);

        AssertEqual("user-result", result.UserId, "user ID should be parsed");
        AssertEqual("Catpaw User", result.AccountLabel, "account label should be parsed");
    }

    private static async Task PinsOfficialOriginAsync()
    {
        var sent = false;
        using var fixture = Fixture(async request =>
        {
            sent = true;
            await AssertRequestAsync(request, HttpMethod.Post, "/api/login/refreshToken",
                """{"refreshToken":"current-refresh-secret"}""", true,
                "current-access-secret", "current-tenant");
            return JsonResponse("""{"code":0,"data":{"accessToken":"rotated-access","refreshToken":"rotated-refresh","accessExpiresAt":"2026-07-11T10:00:00Z","refreshExpiresAt":"2026-08-11T10:00:00Z"}}""");
        }, baseAddress: new Uri("https://untrusted.invalid/capture/"));

        await fixture.Client.RefreshAsync(CurrentSession(), default);

        AssertTrue(sent, "request should be sent through the fake handler");
    }

    private static Task DefaultTimeoutAsync()
    {
        AssertEqual(TimeSpan.FromSeconds(15), CatpawAuthClient.DefaultOperationTimeout,
            "production operation timeout should be exactly fifteen seconds");
        return Task.CompletedTask;
    }

    private static async Task InternalTimeoutAsync()
    {
        var handlerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = Fixture(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.SetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }, httpTimeout: Timeout.InfiniteTimeSpan, operationTimeout: TimeSpan.FromMilliseconds(20));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.CreateQrAsync(default));

        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        AssertTrue(error.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            "internal timeout should have a redacted timeout category");
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

    private static async Task InvalidEnvelopeSchemaAsync()
    {
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(
            """{"code":"0","data":{"verified":true,"invitationCodeRequired":false}}""")));

        await CaptureAuthErrorAsync(() => fixture.Client.VerifySmsAsync(
            "13800138000", "123456", default));
    }

    private static async Task MissingTokenIsRedactedAsync()
    {
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(
            """{"code":0,"data":{"accessToken":"only-access-secret"}}""")));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.BindMobileAsync(
            "qr-code-secret", "13800138000", "123456", "INVITE", default));

        AssertRedacted(error, "only-access-secret", "qr-code-secret", "13800138000", "123456", "INVITE");
    }

    private static async Task NonObjectDataAsync()
    {
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(
            """{"code":0,"data":true}""")));

        await CaptureAuthErrorAsync(() => fixture.Client.VerifySmsAsync(
            "13800138000", "123456", default));
    }

    private static async Task InvalidQrChallengeSchemasAsync()
    {
        var invalidSchemas = new[]
        {
            """{"code":0,"data":{"code":" ","qrcodeUrl":"https://example.invalid/qr","expiresAt":"2026-07-11T09:00:00Z"}}""",
            """{"code":0,"data":{"code":"qr-secret","qrcodeUrl":"not-a-url","expiresAt":"2026-07-11T09:00:00Z"}}""",
            """{"code":0,"data":{"code":"qr-secret","qrcodeUrl":"https://example.invalid/qr"}}""",
        };

        foreach (var schema in invalidSchemas)
        {
            using var fixture = Fixture(_ => Task.FromResult(JsonResponse(schema)));
            var error = await CaptureAuthErrorAsync(() => fixture.Client.CreateQrAsync(default));
            AssertRedacted(error, "qr-secret");
        }
    }

    private static async Task PollingStatesAsync()
    {
        var responses = new Queue<string>(
        [
            """{"code":0,"data":{"status":"pending","mobileBound":true}}""",
            """{"code":0,"data":{"state":"scanned","mobileBound":false}}""",
            SessionEnvelope("mobileBound"),
        ]);
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(responses.Dequeue())));

        var pending = await fixture.Client.PollQrAsync("qr-code-secret", default);
        var scanned = await fixture.Client.PollQrAsync("qr-code-secret", default);
        var mobileBound = await fixture.Client.PollQrAsync("qr-code-secret", default);

        AssertEqual("pending", pending.Status, "pending state should be parsed");
        AssertTrue(pending.Session is null, "pending state should not contain a session");
        AssertEqual("scanned", scanned.Status, "scanned state should be parsed");
        AssertTrue(scanned.RequiresMobileBinding,
            "scanned unbound account should require mobile binding");
        AssertEqual("mobileBound", mobileBound.Status, "mobileBound state should be parsed");
        AssertTrue(mobileBound.Session is not null,
            "mobileBound state with credentials should contain a session");
    }

    private static async Task InvalidPollingSchemasAsync()
    {
        var invalidSchemas = new[]
        {
            """{"code":0,"data":{"mobileBound":true}}""",
            """{"code":0,"data":{"status":7,"mobileBound":true}}""",
            """{"code":0,"data":{"status":"unexpected","mobileBound":true}}""",
            """{"code":0,"data":{"status":"pending","mobileBound":"yes"}}""",
            """{"code":0,"data":{"status":"pending","mobileBound":true,"accessToken":7}}""",
        };

        foreach (var schema in invalidSchemas)
        {
            using var fixture = Fixture(_ => Task.FromResult(JsonResponse(schema)));
            await CaptureAuthErrorAsync(() => fixture.Client.PollQrAsync("qr-code-secret", default));
        }
    }

    private static async Task InvalidSmsSchemasAsync()
    {
        var invalidSchemas = new[]
        {
            """{"code":0,"data":{"uuid":7}}""",
            """{"code":0,"data":{"uuid":"sms-uuid","requestCode":7}}""",
        };

        foreach (var schema in invalidSchemas)
        {
            using var fixture = Fixture(_ => Task.FromResult(JsonResponse(schema)));
            await CaptureAuthErrorAsync(() => fixture.Client.SendSmsAsync(
                "13800138000", "device-uuid", default));
        }
    }

    private static async Task InvalidVerificationSchemasAsync()
    {
        var invalidSchemas = new[]
        {
            """{"code":0,"data":{"verified":1,"invitationCodeRequired":false}}""",
            """{"code":0,"data":{"verified":true,"invitationCodeRequired":"false"}}""",
            """{"code":0,"data":{"verified":true}}""",
        };

        foreach (var schema in invalidSchemas)
        {
            using var fixture = Fixture(_ => Task.FromResult(JsonResponse(schema)));
            await CaptureAuthErrorAsync(() => fixture.Client.VerifySmsAsync(
                "13800138000", "123456", default));
        }
    }

    private static async Task InvalidTokenSchemasAsync()
    {
        var invalidSchemas = new[]
        {
            """{"code":0,"data":{"accessToken":7,"refreshToken":"refresh-secret","userId":"user-result","accountLabel":"Catpaw User"}}""",
            """{"code":0,"data":{"accessToken":"access-secret","refreshToken":" ","userId":"user-result","accountLabel":"Catpaw User"}}""",
        };

        foreach (var schema in invalidSchemas)
        {
            using var fixture = Fixture(_ => Task.FromResult(JsonResponse(schema)));
            var error = await CaptureAuthErrorAsync(() => fixture.Client.LoginMobileAsync(
                "13800138000", "123456", "INVITE", default));
            AssertRedacted(error, "access-secret", "refresh-secret", "13800138000", "123456", "INVITE");
        }
    }

    private static async Task InvalidSessionExpirySchemaAsync()
    {
        using var fixture = Fixture(_ => Task.FromResult(JsonResponse(
            """{"code":0,"data":{"accessToken":"access-secret","refreshToken":"refresh-secret","userId":"user-result","accountLabel":"Catpaw User","accessExpiresAt":true}}""")));

        var error = await CaptureAuthErrorAsync(() => fixture.Client.LoginMobileAsync(
            "13800138000", "123456", null, default));
        AssertRedacted(error, "access-secret", "refresh-secret", "13800138000", "123456");
    }

    private static async Task InvalidIdentitySchemasAsync()
    {
        using (var fixture = Fixture(_ => Task.FromResult(JsonResponse(
                   """{"code":0,"data":{"accessToken":"access-secret","refreshToken":"refresh-secret"}}"""))))
        {
            var error = await CaptureAuthErrorAsync(() => fixture.Client.LoginMobileAsync(
                "13800138000", "123456", null, default));
            AssertRedacted(error, "access-secret", "refresh-secret", "13800138000", "123456");
        }

        using (var fixture = Fixture(_ => Task.FromResult(JsonResponse(
                   """{"code":0,"data":{"accessToken":"access-secret","refreshToken":"refresh-secret","userId":" ","accountLabel":"Catpaw User"}}"""))))
        {
            var error = await CaptureAuthErrorAsync(() => fixture.Client.LoginMobileAsync(
                "13800138000", "123456", null, default));
            AssertRedacted(error, "access-secret", "refresh-secret", "13800138000", "123456");
        }

        using (var fixture = Fixture(_ => Task.FromResult(JsonResponse(
                   """{"code":0,"data":{"userId":"user-result","accountLabel":7}}"""))))
        {
            await CaptureAuthErrorAsync(() => fixture.Client.GetUserInfoAsync(
                "access-request-secret", default));
        }

        var identityless = CurrentSession() with { UserId = "", AccountLabel = "" };
        using (var fixture = Fixture(_ => Task.FromResult(JsonResponse(
                   """{"code":0,"data":{"accessToken":"rotated-access","refreshToken":"rotated-refresh"}}"""))))
        {
            var error = await CaptureAuthErrorAsync(() => fixture.Client.RefreshAsync(
                identityless, default));
            AssertRedacted(error, "rotated-access", "rotated-refresh",
                identityless.AccessToken, identityless.RefreshToken);
        }
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
        catch (OperationCanceledException error)
        {
            AssertEqual(cancellation.Token, error.CancellationToken,
                "caller cancellation token should be preserved");
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
        AssertEqual(new Uri("https://catpaw.meituan.com" + path), request.RequestUri,
            "request must use the exact official absolute URI");
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
        AssertTrue(values!.SequenceEqual([expected]), $"{name} header should match");
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedJson = JsonDocument.Parse(expected);
        try
        {
            using var actualJson = JsonDocument.Parse(actual);
            AssertTrue(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement),
                $"JSON body should match fields: {FieldNames(expectedJson.RootElement)}");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"JSON body should be valid with fields: {FieldNames(expectedJson.RootElement)}");
        }
    }

    private static string FieldNames(JsonElement element) => element.ValueKind == JsonValueKind.Object
        ? string.Join(",", element.EnumerateObject().Select(property => property.Name))
        : "<non-object>";

    private static void AssertSecretEqual(string expected, string actual, string message) =>
        AssertTrue(string.Equals(expected, actual, StringComparison.Ordinal), message);

    private static void AssertSession(AuthSession session)
    {
        AssertSecretEqual("access-result", session.AccessToken, "access token should be parsed");
        AssertSecretEqual("refresh-result", session.RefreshToken, "refresh token should be parsed");
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
        TimeSpan? httpTimeout = null,
        TimeSpan? operationTimeout = null,
        Uri? baseAddress = null) => Fixture(
            (request, _) => response(request), httpTimeout, operationTimeout, baseAddress);

    private static ClientFixture Fixture(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response,
        TimeSpan? httpTimeout = null,
        TimeSpan? operationTimeout = null,
        Uri? baseAddress = null)
    {
        var http = new HttpClient(new DelegateHandler(response))
        {
            BaseAddress = baseAddress ?? new Uri("https://catpaw.meituan.com"),
            Timeout = httpTimeout ?? TimeSpan.FromSeconds(31),
        };
        var client = operationTimeout is null
            ? new CatpawAuthClient(http, Tenant)
            : new CatpawAuthClient(http, Tenant, operationTimeout.Value);
        return new ClientFixture(http, client);
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

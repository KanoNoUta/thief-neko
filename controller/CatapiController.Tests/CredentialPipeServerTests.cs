using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CatapiController;

namespace CatapiController.Tests;

internal static class CredentialPipeServerTests
{
    private const string Tenant = "tenant-1";

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("credential pipe rejects a wrong nonce without echoing secrets",
            WrongNonceIsRedactedAsync);
        yield return ("credential pipe snapshot excludes refresh credentials",
            SnapshotExcludesRefreshCredentialsAsync);
        yield return ("credential pipe status redacts the account label",
            StatusIsRedactedAsync);
        yield return ("credential pipe coalesces concurrent refresh requests",
            ConcurrentRefreshIsCoalescedAsync);
        yield return ("credential pipe rejects malformed and unknown requests",
            MalformedAndUnknownRequestsAreRedactedAsync);
        yield return ("credential pipe rejects oversized requests",
            OversizedRequestIsRejectedAsync);
        yield return ("credential pipe lifecycle is clean and launch values are random",
            LifecycleIsCleanAsync);
    }

    private static async Task WrongNonceIsRedactedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var raw = await SendAsync(fixture.Server.PipeName,
            "{\"nonce\":\"wrong-secret-nonce\",\"operation\":\"snapshot\"}\n");

        using var response = JsonDocument.Parse(raw);
        AssertError(response, "unauthorized");
        AssertSecretFree(raw, "wrong-secret-nonce", fixture.Session);
    }

    private static async Task SnapshotExcludesRefreshCredentialsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var raw = await RequestAsync(fixture.Server, "snapshot");

        using var response = JsonDocument.Parse(raw);
        var snapshot = response.RootElement.GetProperty("snapshot");
        AuthTestSupport.AssertTrue(response.RootElement.GetProperty("ok").GetBoolean(),
            "snapshot should succeed");
        AuthTestSupport.AssertEqual(fixture.Session.AccessToken,
            snapshot.GetProperty("token").GetString(), "snapshot should expose access token");
        AuthTestSupport.AssertEqual(fixture.Session.UserId,
            snapshot.GetProperty("userMis").GetString(), "snapshot should expose user ID");
        AuthTestSupport.AssertEqual(
            $"1d47d6ff96_passportid={fixture.Session.AccessToken}; " +
            $"f32a546874_ssoid={fixture.Session.AccessToken}",
            snapshot.GetProperty("cookie").GetString(),
            "snapshot should derive the Catpaw cookie from the access token");
        AuthTestSupport.AssertTrue(snapshot.GetProperty("generation").GetInt64() > 0,
            "snapshot generation should be positive after login");
        AuthTestSupport.AssertTrue(
            !raw.Contains(fixture.Session.RefreshToken, StringComparison.Ordinal),
            "snapshot must not expose the refresh token");
        AuthTestSupport.AssertTrue(!raw.Contains("accountLabel", StringComparison.OrdinalIgnoreCase),
            "snapshot must not expose account metadata");
    }

    private static async Task StatusIsRedactedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var raw = await RequestAsync(fixture.Server, "status");

        using var response = JsonDocument.Parse(raw);
        var status = response.RootElement.GetProperty("status");
        AuthTestSupport.AssertTrue(status.GetProperty("signedIn").GetBoolean(),
            "status should report signed in");
        AuthTestSupport.AssertEqual("SignedIn", status.GetProperty("state").GetString(),
            "status should expose the redacted state");
        AuthTestSupport.AssertEqual("available",
            status.GetProperty("accountLabelStatus").GetString(),
            "status should expose only label availability");
        AssertSecretFree(raw, fixture.Session.AccountLabel, fixture.Session);
    }

    private static async Task ConcurrentRefreshIsCoalescedAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(async (session, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return session with { AccessToken = "new-access-token" };
        });

        var first = RequestAsync(fixture.Server, "refresh", fixture.Session.AccessToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = RequestAsync(fixture.Server, "refresh", fixture.Session.AccessToken);
        await Task.Delay(50);
        release.TrySetResult();
        var responses = await Task.WhenAll(first, second);

        AuthTestSupport.AssertEqual(1, fixture.Client.RefreshCalls,
            "concurrent broker refreshes should share one service client call");
        foreach (var raw in responses)
        {
            using var response = JsonDocument.Parse(raw);
            AuthTestSupport.AssertEqual("new-access-token",
                response.RootElement.GetProperty("snapshot").GetProperty("token").GetString(),
                "refresh should return the current snapshot");
        }
    }

    private static async Task MalformedAndUnknownRequestsAreRedactedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var malformed = await SendAsync(fixture.Server.PipeName, "{not-json}\n");
        var unknown = await SendAsync(fixture.Server.PipeName, JsonSerializer.Serialize(new
        {
            nonce = fixture.Server.Nonce,
            operation = "steal-refresh-secret",
        }) + "\n");

        using var malformedResponse = JsonDocument.Parse(malformed);
        using var unknownResponse = JsonDocument.Parse(unknown);
        AssertError(malformedResponse, "malformed");
        AssertError(unknownResponse, "unknown");
        AssertSecretFree(malformed + unknown, fixture.Server.Nonce, fixture.Session);
        AuthTestSupport.AssertTrue(!unknown.Contains("steal-refresh-secret", StringComparison.Ordinal),
            "unknown operation must not be echoed");
    }

    private static async Task OversizedRequestIsRejectedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var raw = await SendAsync(fixture.Server.PipeName,
            new string('x', CredentialPipeServer.MaxMessageBytes + 1) + "\n");

        using var response = JsonDocument.Parse(raw);
        AssertError(response, "oversize");
        AssertSecretFree(raw, fixture.Server.Nonce, fixture.Session);
    }

    private static async Task LifecycleIsCleanAsync()
    {
        await using var first = await Fixture.CreateAsync();
        await using var second = await Fixture.CreateAsync();
        AuthTestSupport.AssertTrue(first.Server.PipeName != second.Server.PipeName,
            "pipe names should be random per launch");
        AuthTestSupport.AssertTrue(first.Server.Nonce != second.Server.Nonce,
            "nonces should be random per launch");
        AuthTestSupport.AssertTrue(first.Server.PipeName.All(
                c => char.IsAsciiLetterOrDigit(c) || c == '-'),
            "pipe name should contain only safe characters");

        await first.Server.StartAsync();
        await first.Server.DisposeAsync();
        await first.Server.DisposeAsync();
        await AssertThrowsAsync<Exception>(
            () => SendAsync(first.Server.PipeName,
                JsonSerializer.Serialize(new { nonce = first.Server.Nonce, operation = "status" }) + "\n",
                TimeSpan.FromMilliseconds(200)),
            "disposed pipe should stop accepting connections");
    }

    private static Task<string> RequestAsync(
        CredentialPipeServer server,
        string operation,
        string? usedToken = null) => SendAsync(server.PipeName, JsonSerializer.Serialize(new
        {
            nonce = server.Nonce,
            operation,
            usedToken,
        }) + "\n");

    private static async Task<string> SendAsync(
        string pipeName,
        string request,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        var bytes = Encoding.UTF8.GetBytes(request);
        await client.WriteAsync(bytes, cancellation.Token);
        await client.FlushAsync(cancellation.Token);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync(cancellation.Token)
            ?? throw new InvalidOperationException("pipe closed without a response");
    }

    private static void AssertError(JsonDocument response, string code)
    {
        AuthTestSupport.AssertTrue(!response.RootElement.GetProperty("ok").GetBoolean(),
            "error response should set ok=false");
        AuthTestSupport.AssertEqual(2, response.RootElement.EnumerateObject().Count(),
            "error response should contain only ok and error");
        AuthTestSupport.AssertEqual(code,
            response.RootElement.GetProperty("error").GetString(),
            "error response should use an exact stable string code");
    }

    private static void AssertSecretFree(string raw, string secret, AuthSession session)
    {
        foreach (var value in new[]
        {
            secret,
            session.AccessToken,
            session.RefreshToken,
            session.AccountLabel,
            session.Tenant,
        }.Where(value => !string.IsNullOrEmpty(value)))
        {
            AuthTestSupport.AssertTrue(!raw.Contains(value, StringComparison.Ordinal),
                "redacted response must not contain secret or account values");
        }
    }

    private static async Task<T> AssertThrowsAsync<T>(Func<Task> action, string message)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AuthTemporaryDirectory _directory;

        private Fixture(
            AuthTemporaryDirectory directory,
            FakeAuthClient client,
            CatpawAuthService service,
            CredentialPipeServer server,
            AuthSession session)
        {
            _directory = directory;
            Client = client;
            Service = service;
            Server = server;
            Session = session;
        }

        public FakeAuthClient Client { get; }
        public CatpawAuthService Service { get; }
        public CredentialPipeServer Server { get; }
        public AuthSession Session { get; }

        public static async Task<Fixture> CreateAsync(
            Func<AuthSession, CancellationToken, Task<AuthSession>>? refresh = null)
        {
            var directory = new AuthTemporaryDirectory();
            var client = new FakeAuthClient(refresh);
            var service = new CatpawAuthService(
                client,
                new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
                Tenant);
            var session = new AuthSession(
                "access-secret-token",
                "refresh-secret-token",
                "user-1",
                "Private Account Label",
                Tenant,
                DateTimeOffset.UtcNow.AddMinutes(30),
                DateTimeOffset.UtcNow.AddDays(30),
                DateTimeOffset.UtcNow);
            await service.SaveLoginAsync(session, default);
            var server = new CredentialPipeServer(service, TimeSpan.FromSeconds(1));
            await server.StartAsync();
            return new Fixture(directory, client, service, server, session);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Service.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class FakeAuthClient(
        Func<AuthSession, CancellationToken, Task<AuthSession>>? refresh) : ICatpawAuthClient
    {
        private readonly Func<AuthSession, CancellationToken, Task<AuthSession>> _refresh =
            refresh ?? ((session, _) => Task.FromResult(session));
        private int _refreshCalls;

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public Task<AuthSession> RefreshAsync(AuthSession current, CancellationToken ct)
        {
            Interlocked.Increment(ref _refreshCalls);
            return _refresh(current, ct);
        }

        public Task<AccountInfo> GetUserInfoAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult(new AccountInfo("user-1", "Private Account Label"));
    }
}

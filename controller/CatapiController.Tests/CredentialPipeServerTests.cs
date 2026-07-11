using System.IO.Pipes;
using System.Buffers;
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
        yield return ("credential pipe frame codec handles fragments and exact limits",
            FrameCodecHandlesFragmentsAndLimitsAsync);
        yield return ("credential pipe frame codec clears temporary buffers",
            FrameCodecClearsTemporaryBuffersAsync);
        yield return ("credential pipe start waits for its first listener",
            StartWaitsForFirstListenerAsync);
        yield return ("credential pipe startup factory failures propagate immediately",
            StartupFactoryFailurePropagatesAsync);
        yield return ("credential pipe terminal accept failures remain observable",
            TerminalAcceptFailureIsObservableAsync);
        yield return ("credential pipe disposal cancels a stalled connection",
            DisposalCancelsStalledConnectionAsync);
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
        var exact = await SendAsync(fixture.Server.PipeName,
            new string('x', CredentialPipeServer.MaxMessageBytes) + "\n");
        var oversized = await SendAsync(fixture.Server.PipeName,
            new string('x', CredentialPipeServer.MaxMessageBytes + 1) + "\n");

        using var exactResponse = JsonDocument.Parse(exact);
        using var oversizedResponse = JsonDocument.Parse(oversized);
        AssertError(exactResponse, "malformed");
        AssertError(oversizedResponse, "oversize");
        AssertSecretFree(exact + oversized, fixture.Server.Nonce, fixture.Session);
    }

    private static async Task FrameCodecHandlesFragmentsAndLimitsAsync()
    {
        var exactPayload = Encoding.UTF8.GetBytes(
            new string('x', CredentialPipeServer.MaxMessageBytes));
        await using var exactStream = new ChunkedReadStream(
            exactPayload[..1],
            exactPayload[1..100],
            exactPayload[100..],
            "\n"u8.ToArray());
        var codec = new CredentialPipeFrameCodec();

        var exact = await codec.ReadAsync(exactStream, default);
        AuthTestSupport.AssertEqual(CredentialPipeFrameStatus.Complete, exact.Status,
            "exact payload should be accepted across fragments");
        AuthTestSupport.AssertEqual(CredentialPipeServer.MaxMessageBytes,
            exact.Value!.Length, "newline should not count toward the payload cap");

        var oversizedPayload = Encoding.UTF8.GetBytes(
            new string('x', CredentialPipeServer.MaxMessageBytes + 1) + "\n");
        await using var oversizedStream = new ChunkedReadStream(oversizedPayload);
        var oversized = await codec.ReadAsync(oversizedStream, default);
        AuthTestSupport.AssertEqual(CredentialPipeFrameStatus.Oversize, oversized.Status,
            "payload one byte over the cap should be rejected");
    }

    private static async Task FrameCodecClearsTemporaryBuffersAsync()
    {
        var pool = new TrackingArrayPool();
        var accumulator = new InspectableMemoryStream();
        var codec = new CredentialPipeFrameCodec(pool, () => accumulator);
        await using var stream = new ChunkedReadStream(
            Encoding.UTF8.GetBytes("credential-secret\n"));

        var result = await codec.ReadAsync(stream, default);

        AuthTestSupport.AssertEqual("credential-secret", result.Value,
            "codec should decode the payload before clearing temporary storage");
        AuthTestSupport.AssertTrue(pool.WasZeroedOnReturn,
            "rented read array should be zeroed before return");
        AuthTestSupport.AssertTrue(accumulator.WasZeroedOnDispose,
            "MemoryStream backing data should be zeroed before disposal");
    }

    private static async Task StartWaitsForFirstListenerAsync()
    {
        await using var fixture = await Fixture.CreateAsync(
            serverFactory: service => new CredentialPipeServer(
                service,
                TimeSpan.FromSeconds(1),
                CreateListener),
            start: false);

        await fixture.Server.StartAsync();
        var raw = await RequestAsync(fixture.Server, "status");

        using var response = JsonDocument.Parse(raw);
        AuthTestSupport.AssertTrue(response.RootElement.GetProperty("ok").GetBoolean(),
            "connection should succeed immediately after StartAsync returns");
    }

    private static async Task StartupFactoryFailurePropagatesAsync()
    {
        var expected = new InvalidOperationException("listener startup failed");
        await using var fixture = await Fixture.CreateAsync(
            serverFactory: service => new CredentialPipeServer(
                service,
                TimeSpan.FromSeconds(1),
                _ => throw expected),
            start: false);

        var actual = await AssertThrowsAsync<InvalidOperationException>(
            () => fixture.Server.StartAsync(),
            "startup listener factory failure should propagate");

        AuthTestSupport.AssertTrue(ReferenceEquals(expected, actual),
            "StartAsync should propagate the original startup exception");
    }

    private static async Task TerminalAcceptFailureIsObservableAsync()
    {
        var terminal = new InvalidOperationException("terminal listener failure");
        var factoryCalls = 0;
        await using var fixture = await Fixture.CreateAsync(
            serverFactory: service => new CredentialPipeServer(
                service,
                TimeSpan.FromSeconds(1),
                pipeName => Interlocked.Increment(ref factoryCalls) == 1
                    ? CreateListener(pipeName)
                    : throw terminal),
            start: false);
        await fixture.Server.StartAsync();

        var raw = await RequestAsync(fixture.Server, "status");
        using var response = JsonDocument.Parse(raw);
        AuthTestSupport.AssertTrue(response.RootElement.GetProperty("ok").GetBoolean(),
            "accepted connection should finish before terminal listener failure is reported");
        var actual = await AssertThrowsAsync<InvalidOperationException>(
            () => fixture.Server.Completion,
            "terminal listener failure should remain observable");
        AuthTestSupport.AssertTrue(ReferenceEquals(terminal, actual),
            "completion should expose the original terminal exception");
    }

    private static async Task DisposalCancelsStalledConnectionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var client = new NamedPipeClientStream(
            ".", fixture.Server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await client.ConnectAsync(connectCancellation.Token);

        await fixture.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
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

    private static NamedPipeServerStream CreateListener(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
            Func<AuthSession, CancellationToken, Task<AuthSession>>? refresh = null,
            Func<CatpawAuthService, CredentialPipeServer>? serverFactory = null,
            bool start = true)
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
            var server = serverFactory?.Invoke(service)
                ?? new CredentialPipeServer(service, TimeSpan.FromSeconds(1));
            if (start)
            {
                await server.StartAsync();
            }
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

    private sealed class ChunkedReadStream(params byte[][] chunks) : Stream
    {
        private int _index;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_index >= chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            var chunk = chunks[_index++];
            var count = Math.Min(chunk.Length - _offset, buffer.Length);
            chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset < chunk.Length)
            {
                _index--;
            }
            else
            {
                _offset = 0;
            }
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        public bool WasZeroedOnReturn { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            var buffer = new byte[minimumLength];
            Array.Fill(buffer, (byte)0x5a);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            WasZeroedOnReturn = array.All(value => value == 0);
        }
    }

    private sealed class InspectableMemoryStream()
        : MemoryStream(CredentialPipeFrameCodec.MaxPayloadBytes)
    {
        public bool WasZeroedOnDispose { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasZeroedOnDispose = GetBuffer().All(value => value == 0);
            base.Dispose(disposing);
        }
    }
}

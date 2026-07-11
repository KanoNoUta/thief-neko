using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CatapiController;

internal sealed class CredentialPipeServer : IAsyncDisposable
{
    internal const int MaxMessageBytes = CredentialPipeFrameCodec.MaxPayloadBytes;

    private readonly CatpawAuthService _authService;
    private readonly CredentialPipeFrameCodec _frameCodec = new();
    private readonly TimeSpan _operationTimeout;
    private readonly Func<string, NamedPipeServerStream> _pipeFactory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _acceptLoop;
    private Task? _disposeTask;
    private bool _disposed;

    internal CredentialPipeServer(
        CatpawAuthService authService,
        TimeSpan? operationTimeout = null,
        Func<string, NamedPipeServerStream>? pipeFactory = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2);
        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
        _pipeFactory = pipeFactory ?? CreatePipe;

        PipeName = $"catapi-credential-{Guid.NewGuid():N}";
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    internal string PipeName { get; }
    internal string Nonce { get; }
    internal Task Completion
    {
        get
        {
            lock (_sync)
            {
                return _acceptLoop ?? Task.CompletedTask;
            }
        }
    }

    internal Task StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acceptLoop is null)
            {
                var firstListener = _pipeFactory(PipeName);
                _acceptLoop = AcceptConnectionsAsync(
                    firstListener,
                    _lifetimeCancellation.Token);
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
            _disposeTask = DisposeCoreAsync(_acceptLoop);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task AcceptConnectionsAsync(
        NamedPipeServerStream listener,
        CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await listener.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await listener.DisposeAsync();
                return;
            }
            catch
            {
                await listener.DisposeAsync();
                throw;
            }

            var connection = HandleConnectionAsync(listener, ct);
            lock (_sync)
            {
                _connections.Add(connection);
            }
            _ = ObserveConnectionAsync(connection);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            listener = _pipeFactory(PipeName);
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ObserveConnectionAsync(Task connection)
    {
        try
        {
            await connection;
        }
        catch
        {
        }
        finally
        {
            lock (_sync)
            {
                _connections.Remove(connection);
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken lifetimeToken)
    {
        await using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken))
        {
            timeout.CancelAfter(_operationTimeout);
            try
            {
                var request = await _frameCodec.ReadAsync(pipe, timeout.Token);
                if (request.Status == CredentialPipeFrameStatus.Oversize)
                {
                    await WriteAsync(pipe, Error("oversize"),
                        timeout.Token);
                    return;
                }

                if (request.Status != CredentialPipeFrameStatus.Complete)
                {
                    await WriteAsync(pipe, Error("malformed"),
                        timeout.Token);
                    return;
                }

                var response = await ProcessAsync(request.Value!, timeout.Token);
                await WriteAsync(pipe, response, timeout.Token);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                await TryWriteErrorAsync(pipe, "timeout", lifetimeToken);
            }
            catch
            {
                await TryWriteErrorAsync(pipe, "internal_error", lifetimeToken);
            }
        }
    }

    private async Task<object> ProcessAsync(string line, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "nonce", out var nonce) ||
                !NonceMatches(nonce))
            {
                return Error("unauthorized");
            }

            if (!TryGetString(root, "operation", out var operation))
            {
                return Error("malformed");
            }

            return operation switch
            {
                "snapshot" => SnapshotResponse(await _authService.GetBrokerSnapshotAsync(ct)),
                "refresh" => await RefreshAsync(root, ct),
                "status" => StatusResponse(_authService.GetStatus()),
                _ => Error("unknown"),
            };
        }
        catch (JsonException)
        {
            return Error("malformed");
        }
        catch (InvalidOperationException)
        {
            return Error("unavailable");
        }
        catch (CatpawAuthException)
        {
            return Error("refresh_failed");
        }
    }

    private async Task<object> RefreshAsync(JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "usedToken", out var usedToken))
        {
            return Error("malformed");
        }

        return SnapshotResponse(
            await _authService.RefreshBrokerSnapshotAsync(usedToken, ct));
    }

    private static object SnapshotResponse(BrokerCredentialSnapshot snapshot) => new
    {
        ok = true,
        snapshot = new
        {
            token = snapshot.Token,
            userMis = snapshot.UserMis,
            cookie = snapshot.Cookie,
            generation = snapshot.Generation,
        },
    };

    private static object StatusResponse(AuthStatus status) => new
    {
        ok = true,
        status = new
        {
            signedIn = status.SignedIn,
            state = status.State,
            accountLabelStatus = string.IsNullOrWhiteSpace(status.AccountLabel)
                ? "missing"
                : "available",
        },
    };

    private static object Error(string code) => new
    {
        ok = false,
        error = code,
    };

    private bool NonceMatches(string candidate)
    {
        var expected = Encoding.ASCII.GetBytes(Nonce);
        var actual = Encoding.ASCII.GetBytes(candidate);
        return expected.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static async Task WriteAsync(Stream stream, object response, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        if (bytes.Length > MaxMessageBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                Error("internal_error"));
        }

        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.WriteAsync("\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task TryWriteErrorAsync(
        Stream stream,
        string code,
        CancellationToken lifetimeToken)
    {
        try
        {
            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            writeTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await WriteAsync(stream, Error(code), writeTimeout.Token);
        }
        catch
        {
        }
    }

    private async Task DisposeCoreAsync(Task? acceptLoop)
    {
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop;
            }
            catch
            {
            }
        }

        while (true)
        {
            Task[] connections;
            lock (_sync)
            {
                connections = _connections.ToArray();
            }

            if (connections.Length == 0)
            {
                break;
            }

            await Task.WhenAll(connections.Select(async connection =>
            {
                try
                {
                    await connection;
                }
                catch
                {
                }
            }));
        }

        _lifetimeCancellation.Dispose();
    }
}

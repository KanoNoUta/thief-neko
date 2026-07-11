using System.Buffers;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CatapiController;

internal sealed class CredentialPipeServer : IAsyncDisposable
{
    internal const int MaxMessageBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly CatpawAuthService _authService;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _acceptLoop;
    private Task? _disposeTask;
    private bool _disposed;

    internal CredentialPipeServer(
        CatpawAuthService authService,
        TimeSpan? operationTimeout = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2);
        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        PipeName = $"catapi-credential-{Guid.NewGuid():N}";
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    internal string PipeName { get; }
    internal string Nonce { get; }

    internal Task StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _acceptLoop ??= AcceptConnectionsAsync(_lifetimeCancellation.Token);
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

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch
            {
                await pipe.DisposeAsync();
                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                }
                continue;
            }

            var connection = HandleConnectionAsync(pipe, ct);
            lock (_sync)
            {
                _connections.Add(connection);
            }
            _ = ObserveConnectionAsync(connection);
        }
    }

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
                var requestLine = await ReadLineAsync(pipe, timeout.Token);
                if (requestLine.TooLarge)
                {
                    await WriteAsync(pipe, Error("request_too_large", "Request rejected."),
                        timeout.Token);
                    return;
                }

                if (requestLine.Value is null)
                {
                    await WriteAsync(pipe, Error("bad_request", "Request rejected."),
                        timeout.Token);
                    return;
                }

                var response = await ProcessAsync(requestLine.Value, timeout.Token);
                await WriteAsync(pipe, response, timeout.Token);
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                await TryWriteErrorAsync(pipe, "timeout", "Request timed out.", lifetimeToken);
            }
            catch
            {
                await TryWriteErrorAsync(pipe, "internal_error", "Request failed.", lifetimeToken);
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
                return Error("unauthorized", "Request denied.");
            }

            if (!TryGetString(root, "operation", out var operation))
            {
                return Error("bad_request", "Request rejected.");
            }

            return operation switch
            {
                "snapshot" => SnapshotResponse(await _authService.GetBrokerSnapshotAsync(ct)),
                "refresh" => await RefreshAsync(root, ct),
                "status" => StatusResponse(_authService.GetStatus()),
                _ => Error("unknown_operation", "Operation rejected."),
            };
        }
        catch (JsonException)
        {
            return Error("bad_request", "Request rejected.");
        }
        catch (InvalidOperationException)
        {
            return Error("unavailable", "Credentials unavailable.");
        }
        catch (CatpawAuthException)
        {
            return Error("refresh_failed", "Credential refresh failed.");
        }
    }

    private async Task<object> RefreshAsync(JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "usedToken", out var usedToken))
        {
            return Error("bad_request", "Request rejected.");
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

    private static object Error(string code, string message) => new
    {
        ok = false,
        error = new { code, message },
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

    private static async Task<(string? Value, bool TooLarge)> ReadLineAsync(
        Stream stream,
        CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            using var collected = new MemoryStream();
            while (true)
            {
                var count = await stream.ReadAsync(rented.AsMemory(0, 1024), ct);
                if (count == 0)
                {
                    return (null, false);
                }

                var newline = rented.AsSpan(0, count).IndexOf((byte)'\n');
                var appendCount = newline >= 0 ? newline : count;
                if (collected.Length + appendCount > MaxMessageBytes)
                {
                    return (null, true);
                }

                collected.Write(rented, 0, appendCount);
                if (newline >= 0)
                {
                    try
                    {
                        return (StrictUtf8.GetString(collected.GetBuffer(), 0,
                            checked((int)collected.Length)), false);
                    }
                    catch (DecoderFallbackException)
                    {
                        return (null, false);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task WriteAsync(Stream stream, object response, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        if (bytes.Length + 1 > MaxMessageBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                Error("internal_error", "Response failed."));
        }

        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task TryWriteErrorAsync(
        Stream stream,
        string code,
        string message,
        CancellationToken lifetimeToken)
    {
        try
        {
            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            writeTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await WriteAsync(stream, Error(code, message), writeTimeout.Token);
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
            catch (OperationCanceledException)
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

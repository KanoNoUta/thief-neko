using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CatapiController;

internal sealed class CatpawAuthService : IAsyncDisposable
{
    private sealed record RefreshOutcome(
        AuthSession Session,
        bool PerformedNetworkRefresh);

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    };
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SchedulerFailureDelay = TimeSpan.FromMinutes(5);
    private const int MaxImportOutputCharacters = 64 * 1024;

    private readonly ICatpawAuthClient _client;
    private readonly string _tenant;
    private readonly Func<string, CancellationToken, Task<string>> _desktopStateReader;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task<AuthSession?>> _loadSession;
    private readonly Func<AuthSession, CancellationToken, Task> _saveSession;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private AuthSession? _session;
    private bool _loaded;
    private AuthStatus _status = new(false, string.Empty, "LoginRequired");
    private Task<RefreshOutcome>? _refreshTask;
    private CancellationTokenSource? _schedulerCancellation;
    private Task? _schedulerTask;
    private bool _disposed;

    internal CatpawAuthService(
        ICatpawAuthClient client,
        AuthSessionStore store,
        string tenant,
        Func<string, CancellationToken, Task<string>>? desktopStateReader = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<AuthSession?>>? loadSession = null,
        Func<AuthSession, CancellationToken, Task>? saveSession = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(store);
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _desktopStateReader = desktopStateReader ?? ReadDesktopStateAsync;
        _delay = delay ?? Task.Delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loadSession = loadSession ?? store.LoadAsync;
        _saveSession = saveSession ?? store.SaveAsync;
    }

    public async Task<AuthSession?> GetSessionAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_loaded)
            {
                return _session;
            }
        }

        var loaded = await _loadSession(ct);
        lock (_sync)
        {
            if (!_loaded)
            {
                _session = loaded;
                _loaded = true;
                _status = loaded is null
                    ? new AuthStatus(false, string.Empty, "LoginRequired")
                    : new AuthStatus(true, loaded.AccountLabel, "SignedIn");
            }

            return _session;
        }
    }

    public async Task<AuthSession> ImportDesktopSessionAsync(
        string gatewayPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayPath);
        try
        {
            var json = await _desktopStateReader(gatewayPath, ct);
            var imported = ParseDesktopSession(json);
            var account = await _client.GetUserInfoAsync(imported.AccessToken, ct);
            if (!string.Equals(account.UserId, imported.UserId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(account.AccountLabel))
            {
                throw new InvalidDataException();
            }

            await SaveLoginAsync(imported, ct);
            return imported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("Catpaw desktop session import failed.");
        }
    }

    public Task<AuthSession> RefreshAsync(bool force, CancellationToken ct)
    {
        Task<RefreshOutcome> refresh;
        var joinedActiveRefresh = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_refreshTask is null || _refreshTask.IsCompleted)
            {
                _refreshTask = RefreshCoreAsync(force, _lifetimeCancellation.Token);
            }
            else
            {
                joinedActiveRefresh = true;
            }

            refresh = _refreshTask;
        }

        return force && joinedActiveRefresh
            ? JoinForForcedRefreshAsync(refresh, ct)
            : GetSessionFromOutcomeAsync(refresh, ct);
    }

    public async Task SaveLoginAsync(AuthSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _saveSession(session, ct);
        lock (_sync)
        {
            ThrowIfDisposed();
            _session = session;
            _loaded = true;
            _status = new AuthStatus(true, session.AccountLabel, "SignedIn");
        }
    }

    public AuthStatus GetStatus()
    {
        lock (_sync)
        {
            return _status;
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await GetSessionAsync(ct);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_schedulerTask is not null && !_schedulerTask.IsCompleted)
            {
                return;
            }

            _schedulerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _schedulerTask = RunSchedulerAsync(_schedulerCancellation.Token);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Task? scheduler;
        lock (_sync)
        {
            scheduler = _schedulerTask;
            _schedulerCancellation?.Cancel();
        }

        if (scheduler is null)
        {
            return;
        }

        try
        {
            await scheduler.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                _schedulerCancellation?.Dispose();
                _schedulerCancellation = null;
                _schedulerTask = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetimeCancellation.Cancel();
        await StopAsync();
        _lifetimeCancellation.Dispose();
    }

    private async Task<RefreshOutcome> RefreshCoreAsync(bool force, CancellationToken ct)
    {
        var current = await GetSessionAsync(ct)
            ?? throw new InvalidOperationException("Catpaw login is required.");
        if (!force && GetRefreshDelay(current) > TimeSpan.Zero)
        {
            return new RefreshOutcome(current, false);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var refreshed = await _client.RefreshAsync(current, ct);
                await _saveSession(refreshed, ct);
                lock (_sync)
                {
                    _session = refreshed;
                    _loaded = true;
                    _status = new AuthStatus(true, refreshed.AccountLabel, "SignedIn");
                }

                return new RefreshOutcome(refreshed, true);
            }
            catch (CatpawAuthException error)
                when (error.Kind == CatpawAuthFailureKind.AuthRejected)
            {
                lock (_sync)
                {
                    _status = new AuthStatus(false, current.AccountLabel, "LoginRequired");
                }

                throw RedactedRefreshFailure(error.Kind);
            }
            catch (Exception error) when (IsTransient(error) && attempt < RetryDelays.Length)
            {
                lock (_sync)
                {
                    _status = new AuthStatus(true, current.AccountLabel, "RefreshPending");
                }

                await _delay(RetryDelays[attempt], ct);
            }
            catch (Exception error) when (IsTransient(error))
            {
                lock (_sync)
                {
                    _status = new AuthStatus(true, current.AccountLabel, "RefreshPending");
                }

                throw RedactedRefreshFailure(CatpawAuthFailureKind.Transient);
            }
            catch (CatpawAuthException error)
            {
                throw RedactedRefreshFailure(error.Kind);
            }
        }
    }

    private static async Task<AuthSession> GetSessionFromOutcomeAsync(
        Task<RefreshOutcome> refresh,
        CancellationToken ct)
    {
        var outcome = await refresh.WaitAsync(ct);
        return outcome.Session;
    }

    private async Task<AuthSession> JoinForForcedRefreshAsync(
        Task<RefreshOutcome> refresh,
        CancellationToken ct)
    {
        var outcome = await refresh.WaitAsync(ct);
        return outcome.PerformedNetworkRefresh
            ? outcome.Session
            : await RefreshAsync(true, ct);
    }

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var session = await GetSessionAsync(ct);
            if (session is null)
            {
                return;
            }

            await _delay(GetRefreshDelay(session), ct);
            try
            {
                await RefreshAsync(true, ct);
            }
            catch (CatpawAuthException error)
                when (error.Kind == CatpawAuthFailureKind.AuthRejected)
            {
                return;
            }
            catch (Exception error) when (IsTransient(error))
            {
                await _delay(SchedulerFailureDelay, ct);
            }
        }
    }

    private TimeSpan GetRefreshDelay(AuthSession session)
    {
        if (session.AccessExpiresAt is null)
        {
            return TimeSpan.FromMinutes(45);
        }

        var delay = session.AccessExpiresAt.Value - _timeProvider.GetUtcNow()
            - TimeSpan.FromMinutes(5);
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private AuthSession ParseDesktopSession(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Count() != 4)
        {
            throw new InvalidDataException();
        }

        return new AuthSession(
            RequiredString(root, "token"),
            RequiredString(root, "refreshToken"),
            RequiredString(root, "userMis"),
            RequiredString(root, "accountLabel"),
            _tenant,
            null,
            null,
            _timeProvider.GetUtcNow());
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException();
        }

        return value.GetString()!;
    }

    private static bool IsTransient(Exception error) =>
        error is HttpRequestException ||
        error is CatpawAuthException
        {
            Kind: CatpawAuthFailureKind.Transient,
        };

    private static CatpawAuthException RedactedRefreshFailure(
        CatpawAuthFailureKind kind) =>
        new("Catpaw session refresh failed.", kind);

    private static async Task<string> ReadDesktopStateAsync(
        string gatewayPath,
        CancellationToken ct)
    {
        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            var scriptPath = Path.Combine(gatewayPath, "src", "catpawState.js");
            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = gatewayPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(scriptPath);

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ImportTimeout);
            stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException();
            }

            return stdout;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("Catpaw desktop state reader failed.");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                await ObserveAsync(stdoutTask);
                await ObserveAsync(stderrTask);
                process.Dispose();
            }
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                return output.ToString();
            }

            if (output.Length + read > MaxImportOutputCharacters)
            {
                throw new InvalidDataException();
            }

            output.Append(buffer, 0, read);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

using System.IO;
using System.Net.Http;

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
    private static readonly TimeSpan SchedulerFailureDelay = TimeSpan.FromMinutes(5);

    private readonly ICatpawAuthClient _client;
    private readonly string _tenant;
    private readonly IDesktopStateReader _desktopStateReader;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task<AuthSession?>> _loadSession;
    private readonly Func<AuthSession, CancellationToken, Task> _saveSession;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private AuthSession? _session;
    private long _sessionGeneration;
    private bool _loaded;
    private AuthStatus _status = new(false, string.Empty, "LoginRequired");
    private TaskCompletionSource _sessionChanged = NewSignal();
    private Task<RefreshOutcome>? _refreshTask;
    private CancellationTokenSource? _schedulerCancellation;
    private Task? _schedulerTask;
    private Task? _schedulerCleanupTask;
    private long _schedulerGeneration;
    private bool _disposed;
    private Task? _disposeTask;

    internal CatpawAuthService(
        ICatpawAuthClient client,
        AuthSessionStore store,
        string tenant,
        IDesktopStateReader? desktopStateReader = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<AuthSession?>>? loadSession = null,
        Func<AuthSession, CancellationToken, Task>? saveSession = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(store);
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _desktopStateReader = desktopStateReader ?? new DesktopStateReader();
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

        await _mutationGate.WaitAsync(ct);
        try
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
                _session = loaded;
                _loaded = true;
                _sessionGeneration++;
                _status = loaded is null
                    ? new AuthStatus(false, string.Empty, "LoginRequired")
                    : new AuthStatus(true, loaded.AccountLabel, "SignedIn");
                SignalSessionChangedLocked();
                return _session;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<AuthSession> ImportDesktopSessionAsync(
        string gatewayPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayPath);
        try
        {
            var desktop = await _desktopStateReader.ReadAsync(gatewayPath, ct);
            var imported = new AuthSession(
                desktop.AccessToken,
                desktop.RefreshToken,
                desktop.UserId,
                desktop.AccountLabel,
                _tenant,
                null,
                null,
                _timeProvider.GetUtcNow());
            var account = await _client.GetUserInfoAsync(imported.AccessToken, ct);
            if (!string.Equals(account.UserId, imported.UserId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(account.AccountLabel))
            {
                throw new InvalidDataException();
            }

            imported = imported with { AccountLabel = account.AccountLabel };
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
        ct.ThrowIfCancellationRequested();
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
        await _mutationGate.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
            }

            await _saveSession(session, ct);
            lock (_sync)
            {
                ThrowIfDisposed();
                _session = session;
                _loaded = true;
                _sessionGeneration++;
                _status = new AuthStatus(true, session.AccountLabel, "SignedIn");
                SignalSessionChangedLocked();
            }
        }
        finally
        {
            _mutationGate.Release();
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
            if (_schedulerTask is not null)
            {
                return;
            }

            var schedulerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            var generation = ++_schedulerGeneration;
            var scheduler = RunSchedulerAsync(schedulerCancellation.Token);
            _schedulerCancellation = schedulerCancellation;
            _schedulerTask = scheduler;
            _schedulerCleanupTask = CleanupSchedulerAsync(
                scheduler, schedulerCancellation, generation);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Task? scheduler;
        Task? cleanup;
        lock (_sync)
        {
            scheduler = _schedulerTask;
            cleanup = _schedulerCleanupTask;
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

        if (cleanup is not null)
        {
            await cleanup.WaitAsync(ct);
        }
    }

    private async Task CleanupSchedulerAsync(
        Task scheduler,
        CancellationTokenSource schedulerCancellation,
        long generation)
    {
        await ObserveAsync(scheduler);
        lock (_sync)
        {
            if (_schedulerGeneration == generation &&
                ReferenceEquals(_schedulerCancellation, schedulerCancellation))
            {
                _schedulerCancellation = null;
                _schedulerTask = null;
                _schedulerCleanupTask = null;
            }
        }

        schedulerCancellation.Dispose();
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
            _disposeTask = DisposeCoreAsync(
                _schedulerTask, _schedulerCleanupTask, _refreshTask);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(
        Task? scheduler,
        Task? schedulerCleanup,
        Task? refresh)
    {
        _lifetimeCancellation.Cancel();
        lock (_sync)
        {
            SignalSessionChangedLocked();
            try
            {
                _schedulerCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await ObserveAsync(scheduler);
        await ObserveAsync(schedulerCleanup);
        await ObserveAsync(refresh);
        await _mutationGate.WaitAsync();
        _mutationGate.Release();
        _lifetimeCancellation.Dispose();
    }

    private async Task<RefreshOutcome> RefreshCoreAsync(bool force, CancellationToken ct)
    {
        await GetSessionAsync(ct);
        AuthSession current;
        long baseGeneration;
        await _mutationGate.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                current = _session
                    ?? throw new InvalidOperationException("Catpaw login is required.");
                baseGeneration = _sessionGeneration;
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (!force && GetRefreshDelay(current) > TimeSpan.Zero)
        {
            return new RefreshOutcome(current, false);
        }

        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                var superseding = await GetSupersedingSessionAsync(baseGeneration, ct);
                if (superseding.Changed)
                {
                    return new RefreshOutcome(
                        superseding.Session ?? throw new InvalidOperationException(
                            "Catpaw login is required."),
                        true);
                }
            }

            try
            {
                var refreshed = await _client.RefreshAsync(current, ct);
                await _mutationGate.WaitAsync(ct);
                try
                {
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        if (_sessionGeneration != baseGeneration)
                        {
                            return new RefreshOutcome(
                                _session ?? throw new InvalidOperationException(
                                    "Catpaw login is required."),
                                true);
                        }
                    }

                    await _saveSession(refreshed, ct);
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        _session = refreshed;
                        _loaded = true;
                        _sessionGeneration++;
                        _status = new AuthStatus(true, refreshed.AccountLabel, "SignedIn");
                        SignalSessionChangedLocked();
                    }
                }
                finally
                {
                    _mutationGate.Release();
                }

                return new RefreshOutcome(refreshed, true);
            }
            catch (CatpawAuthException error)
                when (error.Kind == CatpawAuthFailureKind.AuthRejected)
            {
                var superseding = await GetSupersedingSessionAsync(baseGeneration, ct);
                if (superseding.Changed)
                {
                    return new RefreshOutcome(
                        superseding.Session ?? throw new InvalidOperationException(
                            "Catpaw login is required."),
                        true);
                }

                lock (_sync)
                {
                    _status = new AuthStatus(false, current.AccountLabel, "LoginRequired");
                    SignalSessionChangedLocked();
                }

                throw RedactedRefreshFailure(error.Kind);
            }
            catch (Exception error) when (IsTransient(error) && attempt < RetryDelays.Length)
            {
                var superseding = await GetSupersedingSessionAsync(baseGeneration, ct);
                if (superseding.Changed)
                {
                    return new RefreshOutcome(
                        superseding.Session ?? throw new InvalidOperationException(
                            "Catpaw login is required."),
                        true);
                }

                lock (_sync)
                {
                    _status = new AuthStatus(true, current.AccountLabel, "RefreshPending");
                }

                await _delay(RetryDelays[attempt], ct);
            }
            catch (Exception error) when (IsTransient(error))
            {
                var superseding = await GetSupersedingSessionAsync(baseGeneration, ct);
                if (superseding.Changed)
                {
                    return new RefreshOutcome(
                        superseding.Session ?? throw new InvalidOperationException(
                            "Catpaw login is required."),
                        true);
                }

                lock (_sync)
                {
                    _status = new AuthStatus(true, current.AccountLabel, "RefreshPending");
                }

                throw RedactedRefreshFailure(CatpawAuthFailureKind.Transient);
            }
            catch (CatpawAuthException error)
            {
                var superseding = await GetSupersedingSessionAsync(baseGeneration, ct);
                if (superseding.Changed)
                {
                    return new RefreshOutcome(
                        superseding.Session ?? throw new InvalidOperationException(
                            "Catpaw login is required."),
                        true);
                }

                throw RedactedRefreshFailure(error.Kind);
            }
        }
    }

    private async Task<(bool Changed, AuthSession? Session)> GetSupersedingSessionAsync(
        long baseGeneration,
        CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return (_sessionGeneration != baseGeneration, _session);
            }
        }
        finally
        {
            _mutationGate.Release();
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
            await GetSessionAsync(ct);
            AuthSession? session;
            bool signedIn;
            Task sessionChanged;
            lock (_sync)
            {
                session = _session;
                signedIn = _status.SignedIn;
                sessionChanged = _sessionChanged.Task;
            }

            if (session is null || !signedIn)
            {
                await sessionChanged.WaitAsync(ct);
                continue;
            }

            if (!await WaitForDelayOrSignalAsync(
                    GetRefreshDelay(session), sessionChanged, ct))
            {
                continue;
            }

            try
            {
                await RefreshAsync(true, ct);
            }
            catch (CatpawAuthException error)
                when (error.Kind == CatpawAuthFailureKind.AuthRejected)
            {
                continue;
            }
            catch (Exception error) when (IsTransient(error))
            {
                lock (_sync)
                {
                    sessionChanged = _sessionChanged.Task;
                }

                await WaitForDelayOrSignalAsync(
                    SchedulerFailureDelay, sessionChanged, ct);
            }
        }
    }

    private async Task<bool> WaitForDelayOrSignalAsync(
        TimeSpan duration,
        Task sessionChanged,
        CancellationToken ct)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = _delay(duration, delayCancellation.Token);
        var completed = await Task.WhenAny(delay, sessionChanged);
        if (completed == sessionChanged)
        {
            delayCancellation.Cancel();
            await ObserveAsync(delay);
            ct.ThrowIfCancellationRequested();
            return false;
        }

        await delay;
        return true;
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

    private static bool IsTransient(Exception error) =>
        error is HttpRequestException ||
        error is CatpawAuthException
        {
            Kind: CatpawAuthFailureKind.Transient,
        };

    private static CatpawAuthException RedactedRefreshFailure(
        CatpawAuthFailureKind kind) =>
        new("Catpaw session refresh failed.", kind);

    private static TaskCompletionSource NewSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalSessionChangedLocked()
    {
        var changed = _sessionChanged;
        _sessionChanged = NewSignal();
        changed.TrySetResult();
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

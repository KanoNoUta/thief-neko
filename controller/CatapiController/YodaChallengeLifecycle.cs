namespace CatapiController;

internal sealed class YodaChallengeLifecycle
{
    private readonly Func<CancellationToken, Task> _initialize;
    private readonly Action _dispose;
    private readonly Func<Task> _cleanup;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private Task? _initialization;
    private Task? _closing;

    public YodaChallengeLifecycle(
        Func<CancellationToken, Task> initialize,
        Action dispose,
        Func<Task> cleanup)
    {
        _initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
    }

    public CancellationToken LifetimeToken => _lifetime.Token;

    public Task StartAsync()
    {
        lock (_sync)
        {
            if (_closing is not null)
            {
                return Task.CompletedTask;
            }

            return _initialization ??= InitializeCoreAsync();
        }
    }

    public Task CloseAsync()
    {
        lock (_sync)
        {
            if (_closing is not null)
            {
                return _closing;
            }

            _lifetime.Cancel();
            _closing = CloseCoreAsync(_initialization ?? Task.CompletedTask);
            return _closing;
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await _initialize(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task CloseCoreAsync(Task initialization)
    {
        try
        {
            await initialization;
        }
        catch
        {
        }

        try
        {
            _dispose();
        }
        finally
        {
            try
            {
                await _cleanup();
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }
}

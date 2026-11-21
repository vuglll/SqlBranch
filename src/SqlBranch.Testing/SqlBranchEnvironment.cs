namespace SqlBranch.Testing;

public static class SqlBranchEnvironment
{
    internal static FixtureProcess Process { get; } = new();

    public static int? MaxConcurrentBranches
    {
        get => Process.Limit;
        set => Process.Limit = value;
    }

    public static Action<string> Log { get; set; } = message => Console.Error.WriteLine(message);

    public static Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        FixtureHost.Real().IsAvailableAsync(ct);
}

// A limit change also applies to creates already waiting. Count unrestricted creates
// so setting a limit while branches are live cannot overbook the new limit.
internal sealed class FixtureProcess : IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _prune = new(1);
    private bool _pruned;
    private int _live;
    private int? _limit;

    public void Dispose() => _prune.Dispose();

    internal int? Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
        set
        {
            if (value is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            lock (_gate)
            {
                _limit = value;
                WakeWaiters();
            }
        }
    }

    internal async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (_limit is null || _live < _limit)
                {
                    _live++;
                    return;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            _live--;
            WakeWaiters();
        }
    }

    private void WakeWaiters()
    {
        _changed.SetResult();
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal async Task PruneAsync(Func<Task<bool>> prune, CancellationToken ct)
    {
        await _prune.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pruned)
            {
                _pruned = await prune().ConfigureAwait(false);
            }
        }
        finally { _prune.Release(); }
    }
}

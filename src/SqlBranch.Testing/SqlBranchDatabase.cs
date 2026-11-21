using Microsoft.Data.SqlClient;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Testing;

public sealed class SqlBranchDatabase : IAsyncDisposable
{
    private readonly FixtureHost _host;
    private readonly FixtureProcess _process;
    private readonly FoundPool _pool;
    private readonly Settings _settings;
    private readonly SemaphoreSlim _operation = new(1);
    private bool _disposed;

    private SqlBranchDatabase(FixtureHost host, FixtureProcess process, FoundPool pool, Settings settings,
        string name, string connectionString, string cloneMode, DateTimeOffset expiresAt)
    {
        (_host, _process, _pool, _settings) = (host, process, pool, settings);
        (Name, ConnectionString, CloneMode, ExpiresAt) = (name, connectionString, cloneMode, expiresAt);
    }

    public string Name { get; }
    public string ConnectionString { get; }
    public string CloneMode { get; }
    public DateTimeOffset ExpiresAt { get; }

    public static Task<SqlBranchDatabase> CreateAsync(string image, SqlBranchOptions? options = null, CancellationToken ct = default) =>
        CreateAsync(image, options ?? new(), FixtureHost.Real(), SqlBranchEnvironment.Process,
            () => Guid.NewGuid().ToString("N")[..8], ct);

    internal static async Task<SqlBranchDatabase> CreateAsync(string image, SqlBranchOptions options, FixtureHost host,
        FixtureProcess process, Func<string> suffix, CancellationToken ct = default)
    {
        var labels = options.Validate(image);
        await process.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            var (pool, settings) = await host.DiscoverAsync(options.ConfigFile, ct).ConfigureAwait(false);
            await process.PruneAsync(() => host.PruneAsync(pool, settings.Port, ct), ct).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                var name = $"{options.Prefix}-{suffix()}";
                try
                {
                    var (branch, connection) = await host.CreateAsync(pool, settings, image, name, options.TimeLimit, labels, ct).ConfigureAwait(false);
                    return new(host, process, pool, settings, name, connection, CloneModeNames.Of(branch.Mode), branch.ExpiresAt!.Value);
                }
                catch (SqlBranchException ex) when (ex.Code == ErrorCodes.BranchExists && attempt < 3) { }
            }
        }
        catch (Exception ex)
        {
            process.Release();
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            throw host.Failure(ex, image);
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearPool();
            await _host.ResetAsync(_pool, _settings, Name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            throw _host.Failure(ex);
        }
        finally { _operation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                ClearPool();
                await _host.RemoveAsync(_pool, _settings, Name).ConfigureAwait(false);
            }
            catch (SqlBranchException ex) when (ex.Code == ErrorCodes.BranchNotFound) { }
            catch (Exception ex)
            {
                _host.WriteLog($"Branch {Name} was left. Run 'sqlbranch branch prune' after it expires. {ex.Message}");
            }
            finally { _process.Release(); }
        }
        finally { _operation.Release(); }
    }

    private void ClearPool()
    {
        using var connection = new SqlConnection(ConnectionString);
        SqlConnection.ClearPool(connection);
    }

    public override string ToString() => $"SqlBranchDatabase {Name} ({CloneMode})";
}

public sealed class SqlBranchUnavailableException : InvalidOperationException
{
    internal SqlBranchUnavailableException(string message, string fixCommand) : base(message) => FixCommand = fixCommand;
    public string FixCommand { get; }
}

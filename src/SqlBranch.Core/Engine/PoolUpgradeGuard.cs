using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Engine;

/// <summary>The unfinished-upgrade fence for commands that change pool contents.</summary>
internal static class PoolUpgradeGuard
{
    internal static async Task RequireWritableAsync(CredentialsFile credentials, IEngineSession session, CancellationToken cancellationToken)
    {
        if (credentials.Upgrade is not { State: UpgradeRecord.Running })
        {
            return;
        }

        var running = await EngineLocks.IsHeldWithoutTakingAsync(session, EngineLocks.PoolUpgrade, cancellationToken).ConfigureAwait(false);
        throw new SqlBranchException(ErrorCodes.PoolUpgradeRequired,
            (running ? "A pool upgrade is running." : "The pool upgrade did not finish.") +
            " Commands that change images or branches are refused until it finishes. Run 'sqlbranch pool upgrade --yes' to finish it.");
    }

    /// <summary>
    /// The re-read of the pool format after a command's locks (design D8): reads the credentials again through the engine and
    /// exits 3 <c>pool_upgrade_required</c> when an upgrade is unfinished or running. Returns the fresh file, so the command acts on
    /// the format it has now, not on the one it read before it locked.
    /// </summary>
    internal static async Task<CredentialsFile> RecheckAsync(
        IContainerRuntime runtime, string engineName, string engineImage, PoolLocation pool, IEngineSession session, CancellationToken cancellationToken)
    {
        var fresh = await EngineCredentials.ReadThroughEngineAsync(runtime, engineName, engineImage, pool, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        await RequireWritableAsync(fresh, session, cancellationToken).ConfigureAwait(false);
        return fresh;
    }

    /// <summary>True while the upgrade command is alive: it holds the pool-upgrade lock.</summary>
    internal static Task<bool> IsLiveAsync(IEngineSession session, CancellationToken cancellationToken) =>
        EngineLocks.IsHeldWithoutTakingAsync(session, EngineLocks.PoolUpgrade, cancellationToken);
}

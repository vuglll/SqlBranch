using SqlBranch.Core.Output;

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
}

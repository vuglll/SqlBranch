using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

/// <summary>A removed branch.</summary>
/// <param name="Name">The branch's name.</param>
/// <param name="DatabaseDropped">False when the engine had no database of the branch's own (a create that died, a reset that failed to attach).</param>
/// <param name="Warnings">Things the user should know, such as a database of the same name that is not the branch's and was left alone.</param>
public sealed record BranchRemoval(string Name, bool DatabaseDropped, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>branch rm</c> as design D5 describes it: the name rules, then the check that <c>branch.json</c> exists, then
/// single-user with rollback and a drop, then the delete of the folder. A database is dropped only when it has a file under
/// the branch's folder, so a database that merely shares the name is never touched. A branch whose database is gone
/// (a dead create, a broken reset) still has its folder removed.
/// </summary>
public sealed class BranchRemover(IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor, string engineName = ContainerLabels.EngineName)
{
    public async Task<BranchRemoval> RemoveAsync(FoundPool pool, int fallbackPort, string name, CancellationToken cancellationToken)
    {
        // Before any SQL runs and before any file is touched.
        NameRules.ValidateBranch(name);

        await using var engine = await BranchEngine.OpenAsync(runtime, connector, redactor, engineName, pool, fallbackPort, cancellationToken).ConfigureAwait(false);
        await engine.RequireWritableAsync(cancellationToken).ConfigureAwait(false);
        await engine.TakeBranchLockAsync(name, cancellationToken).ConfigureAwait(false);

        var (exists, _) = await engine.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            throw BranchEngine.NotABranch(name);
        }

        return await RemoveLockedAsync(engine, name, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<BranchRemoval> RemoveLockedAsync(BranchEngine engine, string name, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var dropped = false;
        if (await engine.DatabaseExistsAsync(name, cancellationToken).ConfigureAwait(false))
        {
            if (await engine.OwnedStateAsync(name, cancellationToken).ConfigureAwait(false) is { } state)
            {
                await engine.DropAsync(name, state, cancellationToken).ConfigureAwait(false);
                dropped = true;
            }
            else
            {
                warnings.Add($"A database named '{name}' exists on the engine but has no file under {BranchPaths.Branch(name)}, so it is not this branch's and was left alone.");
            }
        }

        var folder = BranchPaths.Branch(name);
        var deleted = await engine.TryShellAsync($"rm -rf {Quoting.Shell(folder)} && [ ! -e {Quoting.Shell(folder)} ]", cancellationToken).ConfigureAwait(false);
        if (!deleted.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The branch '{name}' is {(dropped ? "dropped from the engine" : "out of use")}, but deleting its folder failed ({BranchEngine.Detail(deleted)}). " +
                $"{folder} is left behind. 'sqlbranch status' reports it. Delete that folder to finish.");
        }

        return new BranchRemoval(name, dropped, warnings);
    }
}

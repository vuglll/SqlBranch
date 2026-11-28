using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

public sealed record PruneFailure(string Name, string Path, string Message);

public sealed record PruneResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Skipped,
    IReadOnlyList<PruneFailure> Failed, IReadOnlyList<string> Warnings);

public sealed class BranchPruner(IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor,
    string engineName = ContainerLabels.EngineName, TimeProvider? clock = null)
{
    public async Task<PruneResult> PruneAsync(FoundPool pool, int fallbackPort, CancellationToken cancellationToken,
        string? owner = null)
    {
        await using (var guard = await BranchEngine.OpenAsync(runtime, connector, redactor, engineName, pool, fallbackPort, cancellationToken).ConfigureAwait(false))
        {
            await guard.RequireWritableAsync(cancellationToken).ConfigureAwait(false);
        }
        var time = clock ?? TimeProvider.System;
        var lister = new BranchLister(runtime, connector, redactor, engineName, time);
        KeyValuePair<string, string>[] filters = owner is null ? [] : [new("sqlbranch.owner", owner)];
        var listing = await lister.ListAsync(pool, fallbackPort, cancellationToken, filters).ConfigureAwait(false);
        var removed = new List<string>();
        var skipped = new List<string>();
        var failed = new List<PruneFailure>();
        var warnings = listing.Warnings.ToList();
        foreach (var candidate in listing.Branches.Where(b => b.Expired))
        {
            var name = candidate.Name;
            try
            {
                await using var engine = await BranchEngine.OpenAsync(runtime, connector, redactor, engineName, pool,
                    fallbackPort, cancellationToken).ConfigureAwait(false);
                if (!await EngineLocks.TryTakeAsync(engine.Session, EngineLocks.Branch(name), cancellationToken).ConfigureAwait(false))
                {
                    skipped.Add(name);
                    continue;
                }

                await engine.RecheckFormatAsync(cancellationToken).ConfigureAwait(false);

                // The original branch could have been removed and the name reused since listing.
                var (_, record) = await engine.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false);
                if (record is not { Ephemeral: true, ExpiresAt: { } expiry } || expiry >= time.GetUtcNow() ||
                    (owner is not null && record.Labels?.GetValueOrDefault("sqlbranch.owner") != owner))
                {
                    continue;
                }

                var removal = await BranchRemover.RemoveLockedAsync(engine, name, cancellationToken).ConfigureAwait(false);
                removed.Add(name);
                warnings.AddRange(removal.Warnings);
            }
            catch (SqlBranchException ex) when (ex.Code == ErrorCodes.PoolUpgradeRequired)
            {
                // The refusal ends the prune; say what it had already removed, which the error would otherwise hide.
                if (removed.Count == 0)
                {
                    throw;
                }

                throw new SqlBranchException(ex.Code, $"{ex.Message} Branches removed before the refusal: {string.Join(", ", removed)}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new PruneFailure(name, BranchPaths.Branch(name), redactor.Redact(ex.Message)));
            }
        }

        return new PruneResult(removed, skipped, failed, warnings);
    }
}

using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

/// <summary>
/// <c>branch reset</c> in the order of design D5, so that a failure before the drop changes nothing:
/// <list type="number">
/// <item>the image named in <c>branch.json</c> exists (held in shared mode, so <c>image rm</c> waits);</item>
/// <item><c>requireThin</c> and, for a copy, the free space for a full copy;</item>
/// <item>the image's files are cloned into <c>/pool/branches/&lt;name&gt;/next/</c>;</item>
/// <item>single-user with rollback, the old database is dropped (which deletes its files);</item>
/// <item><c>next/</c> is moved into place and the database is attached.</item>
/// </list>
/// A failure in the last step leaves <c>branch.json</c> with <c>"state": "broken"</c>; running the reset again retries it from the image.
/// A branch made as a full copy stays a full copy.
/// </summary>
public sealed class BranchResetter
{
    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly Func<string> _newId;
    private readonly TimeProvider _time;

    public BranchResetter(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        string engineName = ContainerLabels.EngineName,
        Func<string>? newId = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
        _newId = newId ?? (() => Guid.NewGuid().ToString("N")[..12]);
    }

    public async Task<BranchCreateResult> ResetAsync(
        FoundPool pool, int fallbackPort, string name, CancellationToken cancellationToken, bool requireThin = false)
    {
        // Before any SQL runs and before any file is touched.
        NameRules.ValidateBranch(name);

        await using var engine = await BranchEngine.OpenAsync(_runtime, _connector, _redactor, _engineName, pool, fallbackPort, cancellationToken).ConfigureAwait(false);
        await engine.RequireWritableAsync(cancellationToken).ConfigureAwait(false);
        await engine.TakeBranchLockAsync(name, cancellationToken).ConfigureAwait(false);

        // The guard: a branch SqlBranch created has a branch.json. Nothing else is ever dropped or changed.
        var (exists, record) = await engine.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            throw BranchEngine.NotABranch(name);
        }

        if (record is null || NameRules.Problem(record.Image, forBranch: false) is not null)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The record of branch '{name}' ({BranchPaths.Json(name)}) cannot be read, so SqlBranch does not know which image to reset it from. Remove the branch with 'sqlbranch branch rm {name}'.");
        }

        // 1. The image exists. Shared: other creates and resets from it run together, while 'image rm' waits.
        var image = record.Image;
        if (!await EngineLocks.TryTakeSharedAsync(engine.Session, EngineLocks.Image(image), cancellationToken).ConfigureAwait(false))
        {
            // An upgrade holds every existing image's lock while it waits (design D8): say so, not "being built or removed".
            await engine.RecheckFormatAsync(cancellationToken).ConfigureAwait(false);
            // An image an older client wrote may have to be adopted by this command, which does not wait for the lock (design D7).
            if (engine.PoolFormat >= 2 && record.BaseKnown is null &&
                await ImageAdoption.BusyIfUnadoptedAsync(_runtime, _engineName, image, cancellationToken).ConfigureAwait(false) is { } busy)
            {
                throw busy;
            }

            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Another command is building or removing the image '{image}'. Wait for it to finish, then run the command again.");
        }

        // The format may have changed since the first read (design D8); nothing has been changed yet.
        await engine.RecheckFormatAsync(cancellationToken).ConfigureAwait(false);

        // On format 2 the base is the version recorded at create (design D5); a removed or unknown base is refused here, before
        // any connection is closed. On format 1 the base is the image.
        string imageDirectory = ".";
        string? version = null;
        ImageManifest manifest;
        if (engine.PoolFormat >= 2)
        {
            var found = await ReadBaseAsync(engine, name, record, cancellationToken).ConfigureAwait(false);
            manifest = found.Manifest;
            imageDirectory = found.Directory;
            version = found.Id;
        }
        else
        {
            manifest = await engine.ReadManifestAsync(
                image,
                $"The image '{image}' that branch '{name}' was created from no longer exists, so the branch cannot be reset. The branch is unchanged. 'sqlbranch image list' shows the images.",
                cancellationToken).ConfigureAwait(false);
        }

        // 2. requireThin (exit 3), then room for a full copy (exit 1): both before any file is made. A branch that was made as a
        // full copy stays one and is exempt from requireThin, as it was at create.
        var poolMode = await CloneProbe.ProbeInEngineAsync(_runtime, cancellationToken, _engineName, _newId()).ConfigureAwait(false);
        // Only a branch made with --copy is exempt; one that is a copy because its pool is in copy mode is not.
        var copyBranch = string.Equals(record.CloneMode, CloneModeNames.Of(CloneMode.Copy), StringComparison.Ordinal);
        BranchGuards.RequireThin(requireThin, poolMode, record.ExplicitCopy);
        var mode = copyBranch ? CloneMode.Copy : poolMode;
        await BranchGuards.RequireSpaceForCopyAsync(_runtime, _engineName, mode, manifest.TotalBytes, $"a full copy of the image '{image}'", cancellationToken).ConfigureAwait(false);

        // A database of this name whose files are not under the branch's folder is somebody else's. Say so before anything is changed.
        var databaseExists = await engine.DatabaseExistsAsync(name, cancellationToken).ConfigureAwait(false);
        var ownState = databaseExists ? await engine.OwnedStateAsync(name, cancellationToken).ConfigureAwait(false) : null;
        var ownDatabase = ownState is not null;
        if (databaseExists && !ownDatabase)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"A database named '{name}' exists on the engine but has no file under {BranchPaths.Branch(name)}, so it is not this branch's and SqlBranch will not drop it. Nothing was changed.");
        }

        // 3. Clone into next/. A leftover next/ of an earlier reset that died is debris: this command holds the branch lock.
        var next = BranchPaths.Next(name);
        try
        {
            await engine.ShellAsync(
                $"set -e; rm -rf {Quoting.Shell(next)}; mkdir {Quoting.Shell(next)}; " + BranchFiles.CloneCommands(manifest, image, mode, next, imageDirectory),
                "cloning the image's files", cancellationToken).ConfigureAwait(false);

            // 4. The old database goes: connections are closed with a rollback and the drop deletes its files.
            if (ownDatabase)
            {
                await engine.DropAsync(name, ownState, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The branch is as it was: remove the prepared files, then report the failure itself.
        catch (Exception)
#pragma warning restore CA1031
        {
            await RemoveNextAsync(engine, next).ConfigureAwait(false);
            throw;
        }

        // From here the old database is gone. The record says so first, so a failure or a crash leaves a branch that can be reset again.
        var data = BranchPaths.Data(name);
        await MarkAsync(engine, name, record with { State = BranchRecord.BrokenState }, cancellationToken).ConfigureAwait(false);

        // 5. Move next/ into place and attach.
        try
        {
            await engine.ShellAsync($"rm -rf {Quoting.Shell(data)} && mv -T {Quoting.Shell(next)} {Quoting.Shell(data)}", "moving the new files into place", cancellationToken).ConfigureAwait(false);
            await engine.Session.ExecuteAsync($"CREATE DATABASE {Quoting.Identifier(name)} ON {BranchFiles.AttachFiles(manifest, data)} FOR ATTACH;", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The old database of branch '{name}' is dropped, but the new files could not be attached: {ex.Message} " +
                $"The branch is marked broken. Run 'sqlbranch branch reset {name}' again to retry it from the image '{image}'.");
        }

        // The branch is attached. A record that cannot be updated is a different failure from an attach that failed.
        try
        {
            await engine.WriteRecordAsync(name, record with { CloneMode = CloneModeNames.Of(mode), State = null, ImageVersion = version ?? record.ImageVersion, BaseKnown = version is null ? record.BaseKnown : true }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Branch '{name}' is reset and its database is attached, but its record {BranchPaths.Json(name)} could not be updated: {ex.Message} " +
                $"'sqlbranch branch list' shows it as broken until the record is written. Run 'sqlbranch branch reset {name}' again to finish.");
        }

        return new BranchCreateResult(name, image, mode, engine.Server, name, BranchCreator.LoginName) { ImageVersion = version ?? record.ImageVersion };
    }

    /// <summary>
    /// The version a format-2 branch is reset to: the recorded one, or, for a record no format-2 command has written (an older
    /// client wrote it after the upgrade), the first version when the branch was created at or after the image's <c>builtAt</c>
    /// (design D7). An image an older client wrote is adopted first, but only once the base is known to be bindable, so a refusal
    /// changes nothing. A removed or unknown base is <c>image_not_found</c> and names the way out.
    /// </summary>
    private async Task<ImageVersionData> ReadBaseAsync(BranchEngine engine, string name, BranchRecord record, CancellationToken cancellationToken)
    {
        var image = record.Image;
        string Refusal(string what) =>
            $"{what} Branch '{name}' is unchanged. Remove it with 'sqlbranch branch rm {name}' and create a new one with 'sqlbranch branch create {name} --from {image}'.";

        if (record.BaseKnown is null)
        {
            var current = await ImageCatalog.ReadVersionAsync(_runtime, _engineName, image, null, cancellationToken).ConfigureAwait(false)
                ?? throw new SqlBranchException(ErrorCodes.ImageNotFound,
                    $"The image '{image}' that branch '{name}' was created from no longer exists, so the branch cannot be reset. The branch is unchanged. 'sqlbranch image list' shows the images.");

            // The version whose files are at the top of the image folder is the one a 0.2.2 record can mean.
            if (current.Directory != "." || record.CreatedAt < current.Manifest.BuiltAt)
            {
                throw new SqlBranchException(ErrorCodes.ImageNotFound,
                    Refusal($"The version of the image '{image}' that branch '{name}' was created from is not known, so that version no longer exists for this branch and the branch cannot be reset."));
            }

            return current.Id is null
                ? await ImageAdoption.AdoptAsync(_runtime, _engineName, engine.Session, image, current, _time, cancellationToken).ConfigureAwait(false)
                : current;
        }

        if (record.BaseKnown == false)
        {
            throw new SqlBranchException(ErrorCodes.ImageNotFound,
                Refusal($"The version of the image '{image}' that branch '{name}' was created from is not known, so that version no longer exists for this branch and the branch cannot be reset."));
        }

        if (record.ImageVersion is null)
        {
            throw new SqlBranchException(ErrorCodes.ImageNotFound,
                Refusal($"The image '{image}' that branch '{name}' was created from was removed before the pool upgrade, so the version it was created from no longer exists and the branch cannot be reset."));
        }

        return await ImageCatalog.ReadVersionAsync(_runtime, _engineName, image, record.ImageVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.ImageNotFound,
                Refusal($"The version {record.ImageVersion} of the image '{image}' that branch '{name}' was created from no longer exists, so the branch cannot be reset."));
    }

    private static async Task RemoveNextAsync(BranchEngine engine, string next)
    {
        try
        {
            await engine.TryShellAsync($"rm -rf {Quoting.Shell(next)}", CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: 'status' reports a next/ that is left behind.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task MarkAsync(BranchEngine engine, string name, BranchRecord broken, CancellationToken cancellationToken)
    {
        try
        {
            await engine.WriteRecordAsync(name, broken, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A branch with no database is listed as broken anyway; the marker is a courtesy.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }
}

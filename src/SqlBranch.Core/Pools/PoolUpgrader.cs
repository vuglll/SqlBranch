using System.Globalization;
using System.Text;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Pools;

/// <summary>An image with the first version the upgrade gave it.</summary>
public sealed record UpgradedImage(string Name, string Version);

/// <summary>The branches of an upgrade by the base they got (design D7): a pinned version, an unknown base, or a removed base.</summary>
public sealed record UpgradedBranches(IReadOnlyList<string> Pinned, IReadOnlyList<string> UnknownBase, IReadOnlyList<string> RemovedBase)
{
    public static UpgradedBranches None { get; } = new([], [], []);
}

/// <summary>What <c>pool upgrade</c> did.</summary>
/// <param name="PreviousFormat">The format before the command: 1, or 2 when the pool was already upgraded.</param>
/// <param name="Images">Each image the upgrade processed with its first version, by name; empty for a pool that was already format 2.</param>
/// <param name="Branches">The branches by the base they got.</param>
public sealed record PoolUpgradeResult(int PreviousFormat, IReadOnlyList<UpgradedImage> Images, UpgradedBranches Branches);

/// <summary>
/// <c>pool upgrade --yes</c> (design D6): the one way to reach pool format 2. Step 0 takes the upgrade lock and reads the
/// format, step 1 writes the fence, step 2 finds and locks the existing images and branches, step 3 writes each image's
/// catalog and high-water mark, step 4 binds branches to their base, step 5 rewrites the fence as done. Every write is a
/// temporary file plus a rename (design D11), so a killed run leaves the old file or the new one, and a rerun continues from
/// what is recorded: it reuses the recorded start time, so IDs already given do not change.
/// </summary>
public sealed class PoolUpgrader
{
    /// <summary>The line printed on standard error, as a <c>warning:</c>, before the first write.</summary>
    public const string Warning =
        "The pool upgrade cannot be undone. SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x packages can no longer use the pool after it.";

    private const string ImageTag = "SQLBRANCH_UPIMAGE|";
    private const string BranchTag = "SQLBRANCH_UPBRANCH|";

    /// <summary>
    /// The existing images (a manifest or a catalog) and branch folders (with or without a <c>branch.json</c>: a create still running has made the folder and writes the record late), one tagged line each. The globs skip
    /// folders that start with a dot (<c>.removing-*</c>, <c>.ids</c>), and an image folder a build is still sealing has no
    /// manifest yet, so it is skipped too, as the 0.2.2 image listing does. An image line carries whether the manifest
    /// exists, the catalog as base64 (or <c>none</c>) and the high-water mark file (or <c>none</c>).
    /// </summary>
    public const string EnumerationScript =
        "for d in /pool/images/*/; do [ -d \"$d\" ] || continue; n=$(basename \"$d\"); m=0; [ -f \"${d}manifest.json\" ] && m=1; " +
        "c=none; if [ -f \"${d}catalog.json\" ]; then c=$(base64 -w0 \"${d}catalog.json\"); fi; " +
        "if [ $m = 0 ] && [ \"$c\" = none ]; then continue; fi; " +
        "i=none; if [ -f \"/pool/images/.ids/$n\" ]; then i=$(cat \"/pool/images/.ids/$n\"); fi; " +
        "echo \"SQLBRANCH_UPIMAGE|$n|$m|$c|$i\"; done; " +
        "for d in /pool/branches/*/; do [ -d \"$d\" ] && echo \"SQLBRANCH_UPBRANCH|$(basename \"$d\")\"; done; true";

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PoolUpgrader(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        string engineName = ContainerLabels.EngineName,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _clock, token));
    }

    /// <summary>The existing images and branches found in step 2.</summary>
    private sealed record Found(IReadOnlyList<FoundImage> Images, IReadOnlyList<string> Branches);

    /// <summary>An existing image as the enumeration saw it: its catalog text (null when it has none) and its high-water mark (null when it has none).</summary>
    private sealed record FoundImage(string Name, bool HasManifest, string? CatalogBase64, string? LastId);

    /// <summary>
    /// Upgrades the pool, or finishes an upgrade that did not finish. <paramref name="progress"/> gets one line at a time for
    /// standard error: the warning first, then progress while waiting for locks. The engine must be running; the caller has checked that.
    /// </summary>
    public async Task<PoolUpgradeResult> UpgradeAsync(FoundPool pool, int fallbackPort, Action<string>? progress, TimeSpan publishWait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var image = ImageCatalog.RequireRunningEngine(pool).Image;

        await using var engine = await BranchEngine.OpenAsync(_runtime, _connector, _redactor, _engineName, pool, fallbackPort, cancellationToken).ConfigureAwait(false);
        var session = engine.Session;

        // Step 0. One upgrade at a time: the lock is held on this session until the command ends.
        if (!await EngineLocks.TryTakeAsync(session, EngineLocks.PoolUpgrade, cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.CatalogBusy,
                "Another `sqlbranch pool upgrade` is running. Wait for it to finish. Nothing was changed.");
        }

        // The format is read again under the lock, so a run that finished while this one waited for the lock ends here.
        var credentials = await EngineCredentials.ReadFileAsync(_runtime, image, pool.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        if (credentials.Format >= 2 && credentials.Upgrade is not { State: UpgradeRecord.Running })
        {
            return new PoolUpgradeResult(credentials.Format, [], UpgradedBranches.None);
        }

        progress?.Invoke($"warning: {Warning}");

        // Step 1. The fence. A rerun finds it already written and keeps its start time, so IDs already given do not change.
        var resumed = credentials.Format >= 2;
        var startedAt = resumed ? credentials.Upgrade!.StartedAt : ImageVersions.ToMillisecond(_clock.GetUtcNow());
        if (!resumed)
        {
            await EngineCredentials.WriteFileAsync(_runtime, image, pool.Location,
                CredentialsFile.Format2(credentials.Password, new UpgradeRecord(startedAt, UpgradeRecord.Running)), cancellationToken).ConfigureAwait(false);
        }

        // Step 2. Freeze the names before waiting; retain acquired locks until the session closes.
        var found = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(session, found, publishWait, progress, cancellationToken).ConfigureAwait(false);

        // The upgrade and item locks protect cleanup from another upgrade or an existing writer.
        await WriteAsync(UpgradeTemporaryFiles.CleanupScript(found.Images.Select(i => i.Name), found.Branches), "removing upgrade temporary files", cancellationToken).ConfigureAwait(false);

        // Step 3. Steps 3 and 4 process exactly the names locked above.
        var images = new List<UpgradedImage>();
        // Refresh metadata after writers finish, but never add newly published names to the locked set.
        var refreshed = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var existing in refreshed.Images.Where(i => found.Images.Any(locked => locked.Name == i.Name)))
        {
            if (await WriteCatalogAsync(existing, startedAt, cancellationToken).ConfigureAwait(false) is { } version)
            {
                images.Add(new UpgradedImage(existing.Name, version));
            }
        }

        // Step 4. Read and bind only the locked branch records.
        var branches = await BindBranchesAsync(engine, found.Branches, images, progress, cancellationToken).ConfigureAwait(false);

        // Step 5.
        await EngineCredentials.WriteFileAsync(_runtime, image, pool.Location,
            CredentialsFile.Format2(credentials.Password, new UpgradeRecord(startedAt, UpgradeRecord.Done)), cancellationToken).ConfigureAwait(false);
        return new PoolUpgradeResult(resumed ? 1 : credentials.Format, images, branches);
    }

    private async Task<Found> EnumerateAsync(CancellationToken cancellationToken)
    {
        var output = await EngineShell.RunAsync(_runtime, _engineName, EnumerationScript, "listing the images and branches", cancellationToken).ConfigureAwait(false);
        var images = new SortedDictionary<string, FoundImage>(StringComparer.Ordinal);
        var branches = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(ImageTag, StringComparison.Ordinal))
            {
                var parts = line.Split('|', 5);
                if (parts.Length == 5 && NameRules.Problem(parts[1], forBranch: false) is null)
                {
                    images[parts[1]] = new FoundImage(parts[1], parts[2] == "1", parts[3] == "none" ? null : parts[3], parts[4] == "none" ? null : parts[4]);
                }
            }
            else if (line.StartsWith(BranchTag, StringComparison.Ordinal))
            {
                var name = line[BranchTag.Length..];
                if (NameRules.Problem(name, forBranch: true) is null)
                {
                    branches.Add(name);
                }
            }
        }

        return new Found([.. images.Values], [.. branches]);
    }

    private async Task LockAsync(IEngineSession session, Found found, TimeSpan wait, Action<string>? progress, CancellationToken cancellationToken)
    {
        var pending = found.Images.Select(i => (Resource: EngineLocks.Image(i.Name), Label: $"image '{i.Name}'"))
            .Concat(found.Branches.Select(b => (Resource: EngineLocks.Branch(b), Label: $"branch '{b}'"))).ToList();
        var start = _clock.GetTimestamp();
        string? lastBusy = null;
        var lastPrinted = start;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in pending.ToArray())
            {
                if (await EngineLocks.TryTakeAsync(session, item.Resource, cancellationToken).ConfigureAwait(false))
                {
                    pending.Remove(item);
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            var busy = string.Join(", ", pending.Select(i => i.Label));
            var remaining = wait - _clock.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                throw new SqlBranchException(ErrorCodes.CatalogBusy,
                    $"The pool upgrade could not lock {busy}: commands are still working on them. " +
                    "Commands that change images or branches are refused until the upgrade finishes. Run 'sqlbranch pool upgrade --yes' again once they finish.");
            }

            if (busy != lastBusy || _clock.GetElapsedTime(lastPrinted) >= TimeSpan.FromSeconds(30))
            {
                progress?.Invoke($"Waiting for {busy} before upgrading the pool.");
                lastBusy = busy;
                lastPrinted = _clock.GetTimestamp();
            }

            await _delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UpgradedBranches> BindBranchesAsync(BranchEngine engine, IReadOnlyList<string> names,
        IReadOnlyList<UpgradedImage> images, Action<string>? progress, CancellationToken cancellationToken)
    {
        var pinned = new List<string>();
        var unknown = new List<string>();
        var removed = new List<string>();
        foreach (var name in names)
        {
            var (exists, record) = await engine.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false);
            // A removal that finished during the wait must not be resurrected.
            if (!exists)
            {
                continue;
            }

            // An unreadable record is left as it is (design D14): writing a base into it could destroy what is left of it.
            if (record is null || NameRules.Problem(record.Image, forBranch: false) is not null)
            {
                progress?.Invoke($"warning: The record of branch '{name}' ({BranchPaths.Json(name)}) cannot be read, so the upgrade left it as it is. " +
                    $"Run 'sqlbranch branch rm {name}' to remove the branch.");
                continue;
            }

            if (record.BaseKnown is null)
            {
                var image = images.FirstOrDefault(i => i.Name == record.Image);
                if (image is null)
                {
                    record = record with { BaseKnown = true, ImageVersion = null };
                }
                else
                {
                    var manifest = await engine.ReadManifestAsync(record.Image,
                        $"The image '{record.Image}' disappeared during the upgrade.", cancellationToken).ConfigureAwait(false);
                    var known = record.CreatedAt >= manifest.BuiltAt;
                    record = record with { BaseKnown = known, ImageVersion = known ? image.Version : null };
                }

                await WriteAsync($"{BranchEngine.RecordWriteScript(name, record)} && {{ sync {Quoting.Shell(BranchPaths.Branch(name))} || true; }}",
                    $"writing the base of branch '{name}'", cancellationToken).ConfigureAwait(false);
            }

            (record.BaseKnown == false ? unknown : record.ImageVersion is null ? removed : pinned).Add(name);
        }

        return new UpgradedBranches(pinned, unknown, removed);
    }

    /// <summary>
    /// Gives an image its first version: the high-water mark first, then the catalog, which is what makes the name a format-2
    /// name. An image whose catalog is already written (an earlier run of this upgrade) keeps it, so IDs do not change.
    /// Returns the version, or null for a name whose catalog lists none.
    /// </summary>
    private async Task<string?> WriteCatalogAsync(FoundImage image, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var name = image.Name;
        if (image.CatalogBase64 is { } text)
        {
            var existing = ParseCatalog(text)
                ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                    $"{ImageCatalog.InvalidCatalog(name).Message} Then run 'sqlbranch pool upgrade --yes' again.");
            return existing.Current;
        }

        DateTimeOffset? last = null;
        if (image.LastId is { } recorded)
        {
            last = ImageVersions.TimeOf(name, recorded) ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The version record of the image '{name}' in {ImageVersions.IdFile(name)} is not valid. " +
                $"Delete that file, or restore it from a copy, then run 'sqlbranch pool upgrade --yes' again.");
        }

        // The first ID is the start time. A name that already had a later ID (a high-water mark above the start time) keeps counting up.
        var id = ImageVersions.Format(name, last is { } l && l > startedAt ? l.AddMilliseconds(1) : startedAt);
        var script = $"set -e; {ImageVersions.HighWaterScript(name, id)}; {ImageVersions.CatalogScript(ImagePaths.Image(name), ImageVersions.FirstCatalog(name, id))}";
        await WriteAsync(script, $"writing the catalog of the image '{name}'", cancellationToken).ConfigureAwait(false);
        return id;
    }

    private static ImageCatalogFile? ParseCatalog(string base64)
    {
        try
        {
            return ImageCatalogFile.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Runs a write in the engine. A full pool is <c>pool_full</c>, and the fence stays <c>running</c> for the rerun.</summary>
    private async Task WriteAsync(string script, string what, CancellationToken cancellationToken)
    {
        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", script], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return;
        }

        var full = result.StdErr.Contains("No space left", StringComparison.OrdinalIgnoreCase);
        throw new SqlBranchException(full ? ErrorCodes.PoolFull : ErrorCodes.OperationFailed,
            $"The pool upgrade stopped while {what} (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)})" +
            $"{(full ? ", because the pool is full. Free space in the pool" : string.Empty)}. " +
            "The upgrade did not finish; run 'sqlbranch pool upgrade --yes' again to finish it.");
    }
}

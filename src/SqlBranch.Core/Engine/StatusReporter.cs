using System.Globalization;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Engine;

/// <summary>One thing <c>status</c> found that does not match.</summary>
/// <param name="Kind">A stable word: <c>branch_missing_database</c>, <c>database_missing_folder</c>, <c>build_database</c>, <c>build_folder</c>, <c>build_credential</c>, <c>reset_files</c> or <c>image_removal</c>.</param>
/// <param name="Name">The branch, image, database or folder it is about.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public sealed record StatusMismatch(string Kind, string Name, string Message);

/// <summary>Total, used and free bytes of the pool.</summary>
public sealed record PoolSpace(long Total, long Used, long Free);

/// <summary>What <c>status</c> reports for a running engine.</summary>
public sealed record StatusReport(
    string Version,
    string Edition,
    int Port,
    string Container,
    FoundPool Pool,
    CloneMode Mode,
    int Images,
    int Branches,
    PoolSpace Space,
    IReadOnlyList<StatusMismatch> Mismatches,
    IReadOnlyList<string> InProgress)
{
    public IReadOnlyList<string> ExpiredBranches { get; init; } = [];
}

/// <summary>
/// Reads the pool and <c>sys.databases</c> and reports anything that does not
/// match (design D3). There is no state file: the pool and the engine are the
/// state. A database counts as a branch database only when its files are under
/// <c>/pool/branches/</c>, so a user database is never a mismatch.
/// </summary>
public sealed class StatusReporter
{
    public const string BranchesRoot = ContainerLabels.PoolMountPath + "/branches/";
    public const string BuildPrefix = "sqlbranch_build_";

    /// <summary>User databases and every file each one has.</summary>
    public const string DatabasesSql =
        "SELECT d.name, f.physical_name FROM sys.databases AS d JOIN sys.master_files AS f ON f.database_id = d.database_id " +
        "WHERE d.database_id > 4 ORDER BY d.name, f.file_id";

    /// <summary>The prefix of each line the listing script prints.</summary>
    private const string Tag = "SQLBRANCH_";

    /// <summary>
    /// Lists what the pool holds, one tagged line each, from inside the engine:
    /// branch folders (with whether they hold <c>branch.json</c> and <c>next/</c>),
    /// images, image removals, build folders and the pool's space.
    /// </summary>
    public const string ListingScript =
        "for d in /pool/branches/*/; do [ -d \"$d\" ] || continue; b=0; x=0; [ -f \"${d}branch.json\" ] && b=1; [ -d \"${d}next\" ] && x=1; " +
        "record=; [ $b = 1 ] && record=$(base64 -w0 \"${d}branch.json\"); echo \"SQLBRANCH_BRANCH|$(basename \"$d\")|$b|$x|$record\"; done; " +
        "for d in /pool/images/*/; do [ -d \"$d\" ] || continue; [ -f \"${d}manifest.json\" ] && echo \"SQLBRANCH_IMAGE|$(basename \"$d\")\"; done; " +
        "for d in /pool/images/.removing-*/; do [ -d \"$d\" ] || continue; echo \"SQLBRANCH_REMOVING|$(basename \"$d\")\"; done; " +
        "for d in /pool/build/*/; do [ -d \"$d\" ] || continue; echo \"SQLBRANCH_BUILD|$(basename \"$d\")\"; done; " +
        BuildCredentials.ListMarkersLoop + "; " +
        "echo \"SQLBRANCH_SPACE|$(df -B1 --output=size,used,avail /pool | tail -n 1)\"";

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly TimeProvider _clock;

    public StatusReporter(
        IContainerRuntime runtime,
        IEngineConnector connector,
        SecretRedactor redactor,
        string engineName = ContainerLabels.EngineName, TimeProvider? clock = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reports on a pool whose engine is running. The password is read from the pool and registered for redaction.</summary>
    public async Task<StatusReport> ReportAsync(FoundPool pool, int fallbackPort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var engine = pool.Engine ?? throw new ArgumentException("The pool has no engine.", nameof(pool));

        var password = await EngineCredentials.ReadAsync(_runtime, engine.Image, pool.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        _redactor.Register(password);

        var port = EngineStarter.PortOf(engine, fallbackPort);
        await using var session = await _connector.ConnectAsync(EngineEndpoint.Loopback(port), password, cancellationToken).ConfigureAwait(false);
        var (version, edition) = await EngineStarter.ReadVersionAsync(session, cancellationToken).ConfigureAwait(false);

        var databases = await ReadDatabasesAsync(session, cancellationToken).ConfigureAwait(false);
        var listing = await ReadListingAsync(cancellationToken).ConfigureAwait(false);
        var mode = await CloneProbe.ProbeInEngineAsync(_runtime, cancellationToken, _engineName).ConfigureAwait(false);

        var mismatches = new List<StatusMismatch>();
        var inProgress = new List<string>();
        await FindMismatchesAsync(session, databases, listing, mismatches, inProgress, cancellationToken).ConfigureAwait(false);

        foreach (var scratch in BacpacScratch.FindAbandoned(engine.Id))
        {
            mismatches.Add(new StatusMismatch("bacpac_scratch", scratch,
                $"Inactive BACPAC client scratch {scratch} remains in the system temp directory."));
        }

        var now = _clock.GetUtcNow();
        return new StatusReport(version, edition, port, _engineName, pool, mode, listing.Images.Count,
            listing.Branches.Count(b => b.HasBranchJson), listing.Space, mismatches, inProgress)
        { ExpiredBranches = listing.Branches.Where(b => b.ExpiresAt < now).Select(b => b.Name).Order(StringComparer.Ordinal).ToArray() };
    }

    private static async Task FindMismatchesAsync(
        IEngineSession session,
        IReadOnlyDictionary<string, List<string>> databases,
        Listing listing,
        List<StatusMismatch> mismatches,
        List<string> inProgress,
        CancellationToken cancellationToken)
    {
        // Each lock is tested once; a name is "running" when another session holds it.
        var tested = new Dictionary<string, bool>(StringComparer.Ordinal);
        async Task<bool> Held(string resource)
        {
            // sp_getapplock takes at most 255 characters. A folder name that long is not one of ours.
            if (resource.Length > 255)
            {
                return false;
            }

            if (!tested.TryGetValue(resource, out var held))
            {
                tested[resource] = held = await EngineLocks.IsHeldAsync(session, resource, cancellationToken).ConfigureAwait(false);
            }

            return held;
        }

        // A branch folder with no database of that name.
        foreach (var branch in listing.Branches)
        {
            if (databases.ContainsKey(branch.Name))
            {
                continue;
            }

            if (await Held(EngineLocks.Branch(branch.Name)).ConfigureAwait(false))
            {
                inProgress.Add($"branch {branch.Name} is being created or reset");
                continue;
            }

            mismatches.Add(new StatusMismatch("branch_missing_database", branch.Name,
                $"The folder {BranchesRoot}{branch.Name} exists but the database {branch.Name} is missing."));
        }

        // A database with files in the branches area and no folder for it.
        var folders = listing.Branches.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, files) in databases)
        {
            var folder = files.Select(FolderOf).FirstOrDefault(f => f is not null);
            if (folder is not null && !folders.Contains(folder))
            {
                mismatches.Add(new StatusMismatch("database_missing_folder", name,
                    $"The database {name} has files under {BranchesRoot} but the folder {BranchesRoot}{folder} is missing."));
            }
        }

        // A reset that is not running leaves next/ behind.
        foreach (var branch in listing.Branches.Where(b => b.HasNext))
        {
            if (await Held(EngineLocks.Branch(branch.Name)).ConfigureAwait(false))
            {
                var note = $"branch {branch.Name} is being created or reset";
                if (!inProgress.Contains(note, StringComparer.Ordinal))
                {
                    inProgress.Add(note);
                }

                continue;
            }

            mismatches.Add(new StatusMismatch("reset_files", branch.Name,
                $"The reset of {branch.Name} did not finish: {BranchesRoot}{branch.Name}/next/ was left behind."));
        }

        // A build that is not running leaves a folder and a temporary database. Both belong to the build id.
        var loginIds = (await BuildLogins.ListIdsAsync(session, cancellationToken, includeLogical: true).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var builds = listing.Builds.Concat(loginIds)
            .Concat(databases.Keys.Where(IsBuildDatabase).Select(n => n[BuildPrefix.Length..]))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var id in builds)
        {
            if (await Held(EngineLocks.Build(id)).ConfigureAwait(false))
            {
                inProgress.Add(BacpacOperation.IsLogicalId(id) ? $"BACPAC build {id} is running" : $"image build {id} is running");
                continue;
            }

            if (BacpacOperation.IsLogicalId(id) && listing.Builds.Contains(id, StringComparer.Ordinal))
            {
                mismatches.Add(new StatusMismatch("bacpac_build", id,
                    $"An inactive BACPAC build may have left client scratch named sqlbranch-bacpac-{id}-* on its original client."));
            }

            if (loginIds.Contains(id))
            {
                mismatches.Add(new StatusMismatch("build_login", BuildPrefix + id, $"The login {BuildPrefix}{id} of an inactive build was left behind."));
            }

            if (databases.ContainsKey(BuildPrefix + id))
            {
                mismatches.Add(new StatusMismatch("build_database", BuildPrefix + id,
                    $"The temporary database {BuildPrefix}{id} of an image build that is not running was left behind."));
            }

            if (listing.Builds.Contains(id, StringComparer.Ordinal))
            {
                mismatches.Add(new StatusMismatch("build_folder", $"{ContainerLabels.PoolMountPath}/build/{id}",
                    $"The staging folder {ContainerLabels.PoolMountPath}/build/{id} of an image build that is not running was left behind."));
            }
        }

        // A storage credential that a dead build recorded and that still exists in the engine. Only the name is shown.
        foreach (var (id, marker) in listing.Credentials.OrderBy(c => c.Marker.Name, StringComparer.Ordinal))
        {
            if (!marker.IsFull || await Held(EngineLocks.Build(id)).ConfigureAwait(false))
            {
                continue;
            }

            if (await BuildCredentials.StillExistsAsync(session, marker, cancellationToken).ConfigureAwait(false))
            {
                mismatches.Add(new StatusMismatch("build_credential", marker.Name,
                    $"The storage credential {marker.Name} of an image build that is not running was left behind. The next 'sqlbranch image build' removes it."));
            }
        }

        // An image removal that stopped halfway.
        foreach (var removing in listing.Removing)
        {
            mismatches.Add(new StatusMismatch("image_removal", removing,
                $"The removal of an image did not finish: {ContainerLabels.PoolMountPath}/images/{removing} is still there."));
        }
    }

    private static bool IsBuildDatabase(string name) => name.StartsWith(BuildPrefix, StringComparison.Ordinal);

    /// <summary>The branch folder a file path is in, or null when it is not under the branches area.</summary>
    private static string? FolderOf(string path)
    {
        if (!path.StartsWith(BranchesRoot, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path[BranchesRoot.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? rest[..slash] : null;
    }

    private static async Task<Dictionary<string, List<string>>> ReadDatabasesAsync(IEngineSession session, CancellationToken cancellationToken)
    {
        var databases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in await session.QueryAsync(DatabasesSql, cancellationToken).ConfigureAwait(false))
        {
            var name = Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? string.Empty;
            var file = Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!databases.TryGetValue(name, out var files))
            {
                databases[name] = files = [];
            }

            files.Add(file);
        }

        return databases;
    }

    private sealed record BranchFolder(string Name, bool HasBranchJson, bool HasNext, DateTimeOffset? ExpiresAt);

    private sealed record Listing(
        IReadOnlyList<BranchFolder> Branches,
        IReadOnlyList<string> Images,
        IReadOnlyList<string> Removing,
        IReadOnlyList<string> Builds,
        IReadOnlyList<(string BuildId, CredentialMarker Marker)> Credentials,
        PoolSpace Space);

    private async Task<Listing> ReadListingAsync(CancellationToken cancellationToken)
    {
        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", ListingScript], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Could not list the pool (exit code {result.ExitCode}).");
        }

        var branches = new List<BranchFolder>();
        var images = new List<string>();
        var removing = new List<string>();
        var builds = new List<string>();
        PoolSpace? space = null;
        var credentials = BuildCredentials.ParseMarkers(result.StdOut);

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(Tag, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|');
            switch (parts[0])
            {
                case "SQLBRANCH_BRANCH" when parts.Length >= 4:
                    var record = parts.Length >= 5 ? BranchLister.ReadRecord(parts[4]) : default;
                    branches.Add(new BranchFolder(parts[1], parts[2] == "1", parts[3] == "1", record.Ephemeral ? record.ExpiresAt : null));
                    break;
                case "SQLBRANCH_IMAGE" when parts.Length >= 2:
                    images.Add(parts[1]);
                    break;
                case "SQLBRANCH_REMOVING" when parts.Length >= 2:
                    removing.Add(parts[1]);
                    break;
                case "SQLBRANCH_BUILD" when parts.Length >= 2:
                    builds.Add(parts[1]);
                    break;
                case "SQLBRANCH_SPACE" when parts.Length >= 2:
                    space = ParseSpace(parts[1]);
                    break;
                default:
                    break;
            }
        }

        return new Listing(branches, images, removing, builds, credentials,
            space ?? throw new SqlBranchException(ErrorCodes.OperationFailed, "Could not read the pool's free space."));
    }

    /// <summary>Parses the pool's <c>df</c> line: size, used and available bytes.</summary>
    internal static PoolSpace? ParseSpace(string text)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 &&
            long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var total) &&
            long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var used) &&
            long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var free)
            ? new PoolSpace(total, used, free)
            : null;
    }
}

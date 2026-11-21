using System.Globalization;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

/// <summary>One branch as <c>branch list</c> shows it.</summary>
/// <param name="Name">The branch folder's name, which is also its database name.</param>
/// <param name="Image">The image its <c>branch.json</c> names, or null when the record cannot be read.</param>
/// <param name="ImageRemoved">The image has no manifest any more (it was removed, with <c>--force</c>). The branch keeps its own files.</param>
/// <param name="CloneMode"><c>thin</c> or <c>copy</c>, from the record, or <c>unknown</c> when the record cannot be read.</param>
/// <param name="CreatedAt">When the branch was created, or null when the record cannot be read.</param>
/// <param name="State"><c>online</c>, <c>offline</c> or <c>broken</c>.</param>
/// <param name="OwnBytes">The space the branch's own data uses beyond blocks shared with other files, or null when it could not be read.</param>
public sealed record BranchEntry(string Name, string? Image, bool ImageRemoved, string CloneMode, DateTimeOffset? CreatedAt, string State, long? OwnBytes)
{
    public bool Ephemeral { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool Expired { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>What the pool holds as branches, and a note for anything that could not be read.</summary>
public sealed record BranchListing(IReadOnlyList<BranchEntry> Branches, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>branch list</c>. A branch is a folder in <c>/pool/branches</c> with a <c>branch.json</c> (design D3: the pool is the
/// state). Its state comes from the engine:
/// <list type="bullet">
/// <item><c>online</c>: a database of that name has files under the branch's folder and the engine reports it ONLINE.</item>
/// <item><c>offline</c>: the same database, reported OFFLINE.</item>
/// <item><c>broken</c>: there is no such database (a create that died after it wrote <c>branch.json</c>, a reset that failed to
/// attach, a database that was dropped by hand), or the record says <c>"state": "broken"</c>, or it cannot be read, or the engine
/// reports a state other than ONLINE or OFFLINE (suspect, recovery pending, restoring).</item>
/// </list>
/// A database in <c>sys.databases</c> with no branch folder is not a branch and is not listed; <c>status</c> reports it.
/// <para>
/// Own changed-data size, read inside the engine container with tools it already has:
/// a <c>copy</c> branch is a full copy, so the size is what <c>du</c> reports for its data folder (allocated bytes). A
/// <c>thin</c> branch's size is the length of the extents that <c>filefrag -v</c> (the FIEMAP ioctl) does not flag
/// <c>shared</c>, that is, the blocks the branch wrote itself (copy-on-write) or that no other file shares any more. After
/// <c>image rm --force</c> the blocks the branch had shared with the image become its own, and the size grows to match.
/// The cost is one <c>filefrag</c> run per file; on a heavily fragmented branch (design D14) its output is long, so a branch with
/// hundreds of thousands of extents takes a second or more to read.
/// </para>
/// </summary>
public sealed class BranchLister
{
    private const string ImageTag = "SQLBRANCH_IMG";
    private const string BranchTag = "SQLBRANCH_BR";

    /// <summary>
    /// Prints one <c>SQLBRANCH_IMG|name</c> line for each image that has a manifest and one
    /// <c>SQLBRANCH_BR|name|branch.json as base64|own bytes</c> line for each branch folder with a <c>branch.json</c>.
    /// The own size is a number, or <c>-</c> when it could not be read. The folder names reach the script only as the
    /// loop variable of a glob under <c>/pool</c>, never as text of the script.
    /// </summary>
    public const string ListingScript =
        "own_thin() { command -v filefrag >/dev/null 2>&1 || { echo -; return; }; " +
        "for f in \"$1\"/*; do [ -f \"$f\" ] && filefrag -v \"$f\" 2>/dev/null; done | awk '" +
        "/ blocks? of [0-9]+ bytes/ { s = $0; sub(/.* blocks? of /, \"\", s); sub(/ bytes.*/, \"\", s); bs = s + 0 } " +
        "/^ *[0-9]+: / { l = $0; gsub(/[.:]/, \" \", l); n = split(l, a, \" \"); if (a[n] !~ /shared/) own += a[6] * bs } " +
        "END { if (bs > 0) print own + 0; else print \"-\" }'; }; " +
        "own_copy() { du -sB1 \"$1\" 2>/dev/null | cut -f1; }; " +
        "for d in /pool/images/*/; do [ -f \"${d}manifest.json\" ] && echo \"SQLBRANCH_IMG|$(basename \"$d\")\"; done; " +
        "for d in /pool/branches/*/; do [ -f \"${d}branch.json\" ] || continue; " +
        "if grep -Eq '\"cloneMode\"[[:space:]]*:[[:space:]]*\"copy\"' \"${d}branch.json\"; then own=$(own_copy \"${d}data\"); else own=$(own_thin \"${d}data\"); fi; " +
        "echo \"SQLBRANCH_BR|$(basename \"$d\")|$(base64 -w0 \"${d}branch.json\")|${own:--}\"; done; true";

    /// <summary>Databases that have a file under <c>/pool/branches/</c>, with the engine's state and each such file.</summary>
    public const string DatabasesSql =
        "SELECT d.name AS name, d.state_desc AS state, f.physical_name AS file_name FROM sys.databases AS d " +
        "JOIN sys.master_files AS f ON f.database_id = d.database_id WHERE LEFT(f.physical_name, LEN(@prefix)) = @prefix";

    private readonly IContainerRuntime _runtime;
    private readonly IEngineConnector _connector;
    private readonly SecretRedactor _redactor;
    private readonly string _engineName;
    private readonly TimeProvider _clock;

    public BranchLister(IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor, string engineName = ContainerLabels.EngineName, TimeProvider? clock = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _engineName = engineName;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BranchListing> ListAsync(FoundPool pool, int fallbackPort, CancellationToken cancellationToken,
        IReadOnlyList<KeyValuePair<string, string>>? filters = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var engine = ImageCatalog.RequireRunningEngine(pool);
        var password = await EngineCredentials.ReadAsync(_runtime, engine.Image, pool.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        _redactor.Register(password);

        await using var session = await _connector.ConnectAsync(EngineEndpoint.Loopback(EngineStarter.PortOf(engine, fallbackPort)), password, cancellationToken).ConfigureAwait(false);
        var rows = await session.QueryRowsAsync(
            DatabasesSql, new Dictionary<string, object?> { ["prefix"] = BranchPaths.BranchesRoot + "/" }, cancellationToken).ConfigureAwait(false);
        var databases = new Dictionary<string, (string State, List<string> Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = Convert.ToString(row["name"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!databases.TryGetValue(name, out var entry))
            {
                databases[name] = entry = (Convert.ToString(row["state"], CultureInfo.InvariantCulture) ?? string.Empty, []);
            }

            entry.Files.Add(Convert.ToString(row["file_name"], CultureInfo.InvariantCulture) ?? string.Empty);
        }

        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", ListingScript], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Could not list the branches in the pool (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
        }

        var images = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<(string Name, string Json, long? Own)>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 4);
            if (parts[0] == ImageTag && parts.Length >= 2)
            {
                images.Add(parts[1]);
            }
            else if (parts[0] == BranchTag && parts.Length >= 4 && NameRules.Problem(parts[1], forBranch: true) is null)
            {
                long? own = long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
                found.Add((parts[1], parts[2], own));
            }
        }

        var warnings = new List<string>();
        var branches = new List<BranchEntry>();
        var now = _clock.GetUtcNow();
        foreach (var (name, encoded, own) in found.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var (image, mode, created, recordedState, labels, ephemeral, expiresAt) = ReadRecord(encoded);
            if (filters is not null && !filters.All(pair => labels is not null &&
                labels.TryGetValue(pair.Key, out var value) && value == pair.Value))
            {
                continue;
            }

            var state = image is null ? "broken" : StateOf(name, recordedState, databases);
            if (image is null)
            {
                warnings.Add($"The record of branch {name} ({BranchPaths.Json(name)}) cannot be read, so its image and clone mode are unknown.");
            }

            if (own is null)
            {
                warnings.Add($"The changed-data size of branch {name} could not be read.");
            }

            branches.Add(new BranchEntry(name, image, image is not null && !images.Contains(image), mode, created, state, own)
            { Ephemeral = ephemeral, ExpiresAt = expiresAt, Expired = ephemeral && expiresAt < now, Labels = labels ?? [] });
        }

        return new BranchListing(branches, warnings);
    }

    private static string StateOf(string name, string? recordedState, Dictionary<string, (string State, List<string> Files)> databases)
    {
        if (string.Equals(recordedState, "broken", StringComparison.OrdinalIgnoreCase))
        {
            return "broken";
        }

        // The database of this branch has its files under this branch's folder; one that merely has the name does not count.
        var folder = BranchPaths.Branch(name) + "/";
        if (!databases.TryGetValue(name, out var database) || !database.Files.Any(f => f.StartsWith(folder, StringComparison.Ordinal)))
        {
            return "broken";
        }

        return database.State.ToUpperInvariant() switch
        {
            "ONLINE" => "online",
            "OFFLINE" => "offline",
            _ => "broken",
        };
    }

    internal static (string? Image, string Mode, DateTimeOffset? Created, string? State, Dictionary<string, string>? Labels, bool Ephemeral, DateTimeOffset? ExpiresAt) ReadRecord(string encoded)
    {
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "unknown", null, null, null, false, null);
            }

            string? Text(string property) =>
                root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var image = Text("image");
            var created = Text("createdAt") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("labels", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        labels[property.Name] = property.Value.GetString()!;
                    }
                }
            }

            return (image, Text("cloneMode") ?? "unknown", created, Text("state"), labels,
                root.TryGetProperty("ephemeral", out var flag) && flag.ValueKind == JsonValueKind.True,
                root.TryGetProperty("expiresAt", out var expiry) && expiry.ValueKind == JsonValueKind.String &&
                expiry.TryGetDateTimeOffset(out var expiresAt) ? expiresAt : null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return (null, "unknown", null, null, null, false, null);
        }
    }
}

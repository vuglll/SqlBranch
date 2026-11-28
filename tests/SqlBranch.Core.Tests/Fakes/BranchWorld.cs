using System.Text;
using System.Text.RegularExpressions;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Images;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// A <see cref="CliWorld"/> with a pool that holds an image and branch folders, for the commands that act on an existing
/// branch (<c>rm</c>, <c>reset</c>). The engine's databases are the connector's <see cref="FakeEngineConnector.Databases"/>:
/// a <c>CREATE DATABASE ... FOR ATTACH</c> adds one, a <c>DROP DATABASE</c> removes one. Nothing reaches the real docker.
/// </summary>
internal sealed partial class BranchWorld
{
    public BranchWorld()
    {
        World = new CliWorld();
        World.WithEngine();

        var inner = World.Runtime.ExecHandler;
        World.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            Timeline.Add("sh: " + script);
            if (script.Contains("SQLBRANCH_POOLFORMAT=", StringComparison.Ordinal))
            {
                return inner(container, command);
            }
            if (script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal) || script.Contains("SQLBRANCH_BRANCH|", StringComparison.Ordinal))
            {
                // The probe, and the listing script of status, which reads the host's branch folders.
                return inner(container, command);
            }

            if (FailScript?.Invoke(script) == true)
            {
                return FakeContainerRuntime.Fail(1, "boom");
            }

            if (script.Contains("SQLBRANCH_RECORD=", StringComparison.Ordinal))
            {
                var name = NameIn(script);
                return FakeContainerRuntime.Ok("SQLBRANCH_RECORD=" + (Records.TryGetValue(name, out var json) ? Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) : "none"));
            }

            if (script.Contains("SQLBRANCH_BR|", StringComparison.Ordinal))
            {
                // The listing script of 'branch list'.
                var lines = (Catalog is null ? Array.Empty<string>() : [$"SQLBRANCH_CAT|{ImageName}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(Catalog))}"])
                    .Concat(ImageExists ? [$"SQLBRANCH_IMG|{ImageName}"] : Array.Empty<string>())
                    .Concat(Records.Select(r => $"SQLBRANCH_BR|{r.Key}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(r.Value))}|1048576"));
                return FakeContainerRuntime.Ok(string.Join('\n', lines) + "\n");
            }

            if (script.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_CATALOGFILE=" + (Catalog is null ? "none" : Convert.ToBase64String(Encoding.UTF8.GetBytes(Catalog))));
            }

            if (script.Contains("SQLBRANCH_MANIFEST=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=" + (ImageExists ? Convert.ToBase64String(Encoding.UTF8.GetBytes(Manifest.ToJson())) : "none"));
            }

            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_FREE=" + FreeBytes);
            }

            if (script.StartsWith("rm -rf '/pool/branches/", StringComparison.Ordinal) && !script.Contains("/next", StringComparison.Ordinal))
            {
                var name = NameIn(script);
                Records.Remove(name);
                World.Host.BranchFolders.RemoveAll(f => f.Name == name);
                return FakeContainerRuntime.Ok();
            }

            if (script.Contains("base64 -d >", StringComparison.Ordinal))
            {
                var b64 = RecordScript().Match(script).Groups["b64"].Value;
                Records[NameIn(script)] = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                return FakeContainerRuntime.Ok();
            }

            return FakeContainerRuntime.Ok();
        };

        var connector = World.Connector;
        connector.RowsHandler = (_, _) =>
            [.. connector.Databases.SelectMany(d => d.Value.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = d.Key,
                ["state"] = States.GetValueOrDefault(d.Key, "ONLINE"),
                ["file_name"] = f,
            }))];
        connector.ExecuteHandler = sql =>
        {
            Timeline.Add("sql: " + sql);
            var altered = AlterTarget().Match(sql);
            if (altered.Success && States.GetValueOrDefault(altered.Groups["n"].Value.Replace("]]", "]", StringComparison.Ordinal), "ONLINE") != "ONLINE")
            {
                throw new InvalidOperationException("Database is not online: ALTER DATABASE failed.");
            }

            OnStatement?.Invoke(sql);
            var attach = Attach().Match(sql);
            if (attach.Success)
            {
                var name = attach.Groups["n"].Value.Replace("]]", "]", StringComparison.Ordinal);
                connector.Databases[name] = [.. Files().Matches(sql).Select(m => m.Groups["f"].Value.Replace("''", "'", StringComparison.Ordinal))];
            }
        };
        connector.ParameterizedScalarHandler = (sql, parameters) =>
        {
            Timeline.Add("sql: " + sql);
            if (sql.Contains("sys.master_files", StringComparison.Ordinal))
            {
                // The state of a database that has a file under the branch's folder; null when it has none.
                var prefix = (string)parameters["p"]!;
                var owned = connector.Databases.TryGetValue((string)parameters["n"]!, out var files) && files.Any(f => f.StartsWith(prefix, StringComparison.Ordinal));
                return owned ? States.GetValueOrDefault((string)parameters["n"]!, "ONLINE") : null;
            }

            if (sql.Contains("state_desc", StringComparison.Ordinal))
            {
                return connector.Databases.ContainsKey((string)parameters["n"]!) ? States.GetValueOrDefault((string)parameters["n"]!, "ONLINE") : null;
            }

            if (sql.Contains("sys.databases", StringComparison.Ordinal))
            {
                return connector.Databases.ContainsKey((string)parameters["n"]!) ? 1 : 0;
            }

            return 1;
        };
    }

    public CliWorld World { get; }

    /// <summary>Everything that happened, in order: <c>sh:</c> scripts in the engine and <c>sql:</c> statements.</summary>
    public List<string> Timeline { get; } = [];

    /// <summary>The <c>branch.json</c> of each branch folder in the pool, by name.</summary>
    public Dictionary<string, string> Records { get; } = [];

    /// <summary>The <c>state_desc</c> of a database, by name; a database not listed is ONLINE. As SQL Server refuses ALTER DATABASE on a database that is not ONLINE, so does the fake.</summary>
    public Dictionary<string, string> States { get; } = [];

    public bool ImageExists { get; set; } = true;

    /// <summary>The name of the one image the fake lists.</summary>
    public string ImageName { get; set; } = "shop";

    /// <summary>The <c>catalog.json</c> of the image <c>shop</c> on a format-2 pool; null means it has none.</summary>
    public string? Catalog { get; set; }

    /// <summary>Makes the pool format 2 with <c>shop</c> at <paramref name="version"/> (null: the name has no current version).</summary>
    public void UseFormat2(string? version)
    {
        Format2Pool.Apply(World);
        Catalog = Format2Pool.Catalog("shop", version);
    }

    public long FreeBytes { get; set; } = 1L << 40;

    /// <summary>Fails the engine script it returns true for, with exit code 1.</summary>
    public Func<string, bool>? FailScript { get; set; }

    /// <summary>Runs on every statement without parameters; it may throw to fail the statement.</summary>
    public Action<string>? OnStatement { get; set; }

    public ImageManifest Manifest { get; set; } = new(
        ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4125.3",
        [new ImageFile("Shop", "Shop.mdf", "data", 8L << 20), new ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)],
        "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero),
        "shop.bak", new string('a', 64), new Dictionary<string, string>(), CliWorld.EngineImage);

    /// <summary>The image's file sizes, which the free-space check of a copy reads.</summary>
    public void ImageOf(long bytes) =>
        Manifest = Manifest with { Files = [new ImageFile("Shop", "Shop.mdf", "data", bytes - 524_288), new ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)] };

    /// <summary>An online branch: its folder with a record, and its database attached from the folder.</summary>
    public void AddBranch(string name, string image = "shop", string mode = "thin", string? state = null, bool explicitCopy = false,
        string? imageVersion = null, bool? baseKnown = null)
    {
        AddBranchWithoutDatabase(name, image, mode, state, explicitCopy);
        if (baseKnown is not null)
        {
            Records[name] = (RecordOf(name) with { ImageVersion = imageVersion, BaseKnown = baseKnown }).ToJson();
        }

        World.Connector.Databases[name] = [$"/pool/branches/{name}/data/Shop.mdf", $"/pool/branches/{name}/data/Shop_log.ldf"];
    }

    /// <summary>A branch folder with a record and no database, as a dead create or a failed reset leaves it.</summary>
    public void AddBranchWithoutDatabase(string name, string image = "shop", string mode = "thin", string? state = null, bool explicitCopy = false)
    {
        Records[name] = RecordJson(image, mode, state, explicitCopy);
        World.Host.BranchFolders.RemoveAll(f => f.Name == name);
        World.Host.BranchFolders.Add((name, true, false));
    }

    public static string RecordJson(string image, string mode, string? state = null, bool explicitCopy = false) =>
        new BranchRecord(image, mode, new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero), new Dictionary<string, string>(), state, explicitCopy).ToJson();

    public BranchRecord RecordOf(string name) => BranchRecord.FromJson(Records[name]);

    public int IndexOf(string marker) => Timeline.FindIndex(l => l.Contains(marker, StringComparison.Ordinal));

    public bool Ran(string marker) => IndexOf(marker) >= 0;

    public (int Exit, string Out, string Err) Run(params string[] args) => World.Run(args);

    private static string NameIn(string script)
    {
        var match = BranchFolder().Match(script);
        return match.Success ? match.Groups["n"].Value : string.Empty;
    }

    [GeneratedRegex(@"printf %s '(?<b64>[^']+)'")]
    private static partial Regex RecordScript();

    [GeneratedRegex(@"/pool/branches/(?<n>[^/'\s]+)")]
    private static partial Regex BranchFolder();

    [GeneratedRegex(@"^ALTER DATABASE \[(?<n>(?:[^\]]|\]\])*)\]")]
    private static partial Regex AlterTarget();

    [GeneratedRegex(@"^CREATE DATABASE \[(?<n>(?:[^\]]|\]\])*)\] ON ")]
    private static partial Regex Attach();

    [GeneratedRegex(@"FILENAME = N'(?<f>(?:[^']|'')*)'")]
    private static partial Regex Files();
}

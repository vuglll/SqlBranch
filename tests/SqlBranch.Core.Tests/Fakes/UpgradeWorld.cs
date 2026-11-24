using System.Text;
using System.Text.RegularExpressions;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// A format-1 pool with images and branches, for <c>pool upgrade</c>. It answers the upgrade's enumeration from the images and
/// branches it holds, applies the high-water mark and catalog writes the upgrade makes, and records every write in order, so a
/// test can check what changed and what did not. Nothing reaches the real docker.
/// </summary>
internal sealed partial class UpgradeWorld
{
    public const string Sentinel = CliWorld.StoredPassword;

    public UpgradeWorld()
    {
        World = new CliWorld();
        World.WithEngine();
        World.Connector.TrackSessionLocks = true;
        // No database of any name exists, so a branch create finds its name free.
        World.Connector.ParameterizedScalarHandler = (sql, _) => sql.Contains("sys.databases", StringComparison.Ordinal) ? 0 : 1;

        var inner = World.Runtime.ExecHandler;
        World.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Join(' ', command);
            Scripts.Add(script);
            if (script == PoolUpgrader.EnumerationScript)
            {
                Writes.Add("enumerate");
                var lines = Images.Values.Where(i => !i.Name.StartsWith('.') && !i.Name.Contains('/') && (i.HasManifest || i.CatalogBase64 is not null)).OrderBy(i => i.Name, StringComparer.Ordinal).Select(i =>
                        $"SQLBRANCH_UPIMAGE|{i.Name}|{(i.HasManifest ? 1 : 0)}|{i.CatalogBase64 ?? "none"}|{i.IdFile ?? "none"}")
                    .Concat(Branches.Where(b => !b.StartsWith('.') && Records.ContainsKey(b)).Order(StringComparer.Ordinal).Select(b => $"SQLBRANCH_UPBRANCH|{b}"));
                return FakeContainerRuntime.Ok(string.Join('\n', lines) + "\n");
            }

            if (script.Contains("SQLBRANCH_RECORD=", StringComparison.Ordinal))
            {
                var name = Regex.Match(script, @"/pool/branches/([^/]+)/branch.json").Groups[1].Value;
                return FakeContainerRuntime.Ok("SQLBRANCH_RECORD=" + (Records.TryGetValue(name, out var json) ? Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) : "none"));
            }

            if (script.Contains("SQLBRANCH_MANIFEST=", StringComparison.Ordinal))
            {
                var name = Regex.Match(script, @"/pool/images/([^/]+)/").Groups[1].Value;
                if (!Images.TryGetValue(name, out var image) || !image.HasManifest)
                {
                    return FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=none");
                }
                var manifest = Convert.ToBase64String(Encoding.UTF8.GetBytes(new SqlBranch.Core.Images.ImageManifest(
                    SqlBranch.Core.Images.ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4125.3",
                    [new SqlBranch.Core.Images.ImageFile("Shop", "Shop.mdf", "data", 8L << 20), new SqlBranch.Core.Images.ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)],
                    "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, image.BuiltAt,
                    "shop.bak", new string('a', 64), new Dictionary<string, string>(), CliWorld.EngineImage).ToJson()));
                return FakeContainerRuntime.Ok("SQLBRANCH_MANIFEST=" + manifest);
            }

            var id = IdWrite().Match(script);
            var catalog = CatalogWrite().Match(script);
            if (id.Success || catalog.Success)
            {
                if (FailWrite?.Invoke(script) is { } stderr)
                {
                    return FakeContainerRuntime.Fail(1, stderr);
                }

                if (id.Success)
                {
                    Images[id.Groups["n"].Value] = Images[id.Groups["n"].Value] with { IdFile = id.Groups["id"].Value };
                    Writes.Add("ids:" + id.Groups["n"].Value);
                }

                if (catalog.Success)
                {
                    var name = catalog.Groups["n"].Value;
                    Images[name] = Images[name] with { CatalogBase64 = catalog.Groups["b64"].Value };
                    Writes.Add("catalog:" + name);
                }

                return FakeContainerRuntime.Ok();
            }

            if (script.Contains("branch.json", StringComparison.Ordinal) && script.Contains("base64 -d", StringComparison.Ordinal))
            {
                var match = Regex.Match(script, @"printf %s '(?<b64>[A-Za-z0-9+/=]+)' \| base64 -d > '/pool/branches/(?<n>[^/']+)/branch\.json\.new'");
                if (match.Success)
                {
                    if (FailWrite?.Invoke(script) is { } error)
                    {
                        return FakeContainerRuntime.Fail(1, error);
                    }
                    var name = match.Groups["n"].Value;
                    Records[name] = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["b64"].Value));
                    Writes.Add("branch:" + name);
                    return FakeContainerRuntime.Ok();
                }
            }

            return inner(container, command);
        };

        var run = World.Runtime.RunHandler;
        World.Runtime.RunHandler = spec =>
        {
            var result = run(spec);
            if (spec.Stdin is { } json && FakeContainerRuntime.ScriptOf(spec).Contains("credentials.json.new", StringComparison.Ordinal))
            {
                Writes.Add(result.Succeeded
                    ? "credentials:" + (json.Contains("\"state\":\"done\"", StringComparison.Ordinal) ? "done" : "running")
                    : "credentials:failed");
            }

            return result;
        };
    }

    public CliWorld World { get; }

    /// <summary>An image folder as the enumeration sees it.</summary>
    public sealed record FakeImage(string Name, bool HasManifest, string? CatalogBase64, string? IdFile)
    {
        public DateTimeOffset BuiltAt { get; init; } = new(2026, 11, 16, 10, 30, 0, TimeSpan.Zero);
        public DateTimeOffset ModifiedAt { get; init; } = new(2026, 11, 30, 10, 30, 0, TimeSpan.Zero);
    }

    public Dictionary<string, FakeImage> Images { get; } = [];

    public List<string> Branches { get; } = [];

    public Dictionary<string, string> Records { get; } = [];

    /// <summary>Every engine script, in order.</summary>
    public List<string> Scripts { get; } = [];

    /// <summary>What happened, in order: <c>enumerate</c>, <c>credentials:running</c>, <c>ids:&lt;name&gt;</c>, <c>catalog:&lt;name&gt;</c>, <c>credentials:done</c>, <c>branch</c>.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Fails the high-water mark and catalog write it returns error text for.</summary>
    public Func<string, string?>? FailWrite { get; set; }

    public void AddImage(string name) => Images[name] = new FakeImage(name, true, null, null);

    public void AddBranch(string name, string image = "shop", DateTimeOffset? createdAt = null)
    {
        Branches.Add(name);
        Records[name] = new BranchRecord(image, "thin", createdAt ?? new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), new Dictionary<string, string>()).ToJson();
    }

    /// <summary>The catalog the upgrade wrote for an image, or null.</summary>
    public string? CatalogOf(string name) =>
        Images.GetValueOrDefault(name)?.CatalogBase64 is { } b64 ? Encoding.UTF8.GetString(Convert.FromBase64String(b64)) : null;

    public (int Exit, string Out, string Err) Run(params string[] args) => World.Run(args);

    [GeneratedRegex(@"printf %s '(?<id>[^']+)' > '/pool/images/\.ids/(?<n>[^']+)\.new'")]
    private static partial Regex IdWrite();

    [GeneratedRegex(@"printf %s '(?<b64>[A-Za-z0-9+/=]+)' \| base64 -d > '/pool/images/(?<n>[^/']+)/catalog\.json\.new'")]
    private static partial Regex CatalogWrite();
}

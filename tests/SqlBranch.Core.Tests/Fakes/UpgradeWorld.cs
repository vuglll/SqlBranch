using System.Text;
using System.Text.RegularExpressions;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Branches;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;

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
            if (script.Contains("SQLBRANCH_POOLFORMAT=", StringComparison.Ordinal))
            {
                return inner(container, command);
            }
            if (script == PoolUpgrader.EnumerationScript)
            {
                Writes.Add("enumerate");
                AfterStep?.Invoke("enumerate");
                var lines = Images.Values.Where(i => !i.Name.StartsWith('.') && !i.Name.Contains('/') && (i.HasManifest || i.CatalogBase64 is not null)).OrderBy(i => i.Name, StringComparer.Ordinal).Select(i =>
                        $"SQLBRANCH_UPIMAGE|{i.Name}|{(i.HasManifest ? 1 : 0)}|{i.CatalogBase64 ?? "none"}|{i.IdFile ?? "none"}")
                    .Concat(Branches.Where(b => !b.StartsWith('.')).Order(StringComparer.Ordinal).Select(b => $"SQLBRANCH_UPBRANCH|{b}"));
                return FakeContainerRuntime.Ok(string.Join('\n', lines) + "\n");
            }

            if (script == ImageCatalog.ListingScript)
            {
                var lines = Images.Values.SelectMany(i =>
                    new[] { $"SQLBRANCH_IMAGE|{i.Name}|{Manifest(i)}", i.CatalogBase64 is null ? "" : $"SQLBRANCH_CATALOG|{i.Name}|{i.CatalogBase64}" })
                    .Concat(Records.Select(b => $"SQLBRANCH_BRANCHJSON|{b.Key}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Value))}"));
                return FakeContainerRuntime.Ok(string.Join('\n', lines));
            }
            if (script.Contains("mv -T", StringComparison.Ordinal) && script.Contains("/pool/images/", StringComparison.Ordinal))
            {
                var name = Regex.Match(script, @"mv -T '/pool/images/([^/']+)'").Groups[1].Value;
                Images.Remove(name);
                return FakeContainerRuntime.Ok("SQLBRANCH_RENAMED=yes");
            }
            if (script == BranchLister.ListingScript)
            {
                return FakeContainerRuntime.Ok(string.Join('\n', Records.Select(b =>
                    $"SQLBRANCH_BR|{b.Key}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Value))}|0")));
            }

            if (script.StartsWith(UpgradeTemporaryFiles.CleanupPrefix, StringComparison.Ordinal))
            {
                // Only the exact paths the script names are removed, like the real shell.
                foreach (var path in TemporaryFiles.Keys.Where(p => script.Contains("'" + p + "'", StringComparison.Ordinal)).ToArray())
                {
                    TemporaryFiles.Remove(path);
                    DeletedFiles.Add(path);
                }
                AfterStep?.Invoke("cleanup");
                return FakeContainerRuntime.Ok();
            }

            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_FREE=" + (100L << 30));
            }

            if (script == StatusReporter.ListingScript)
            {
                var result = inner(container, command);
                return result with { StdOut = result.StdOut + "\n" + string.Join('\n', TemporaryFiles.Keys.Select(p => "SQLBRANCH_UPGRADETEMP|" + p)) };
            }

            if (script.Contains("SQLBRANCH_LASTID=", StringComparison.Ordinal))
            {
                var name = Regex.Match(script, @"/pool/images/\.ids/([^'/]+)'").Groups[1].Value;
                return FakeContainerRuntime.Ok("SQLBRANCH_LASTID=" + (LastIds.GetValueOrDefault(name) ?? Images.GetValueOrDefault(name)?.IdFile ?? "none"));
            }

            if (script.Contains("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))
            {
                var image = Images.GetValueOrDefault(Regex.Match(script, @"/pool/images/([^/']+)/").Groups[1].Value);
                return FakeContainerRuntime.Ok("SQLBRANCH_CATALOGFILE=" + (image?.CatalogBase64 ?? (script.Contains("=legacy", StringComparison.Ordinal) && image is { HasManifest: true } ? "legacy" : "none")));
            }

            if (script.Contains("SQLBRANCH_ADOPT=", StringComparison.Ordinal))
            {
                var image = Images.GetValueOrDefault(Regex.Match(script, @"/pool/images/([^/']+)/").Groups[1].Value);
                return FakeContainerRuntime.Ok("SQLBRANCH_ADOPT=" + (image?.CatalogBase64 is not null ? "catalog" : image is { HasManifest: true } ? "unadopted" : "none"));
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
                var manifest = Manifest(image);
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
                    var name = id.Groups["n"].Value;
                    LastIds[name] = id.Groups["id"].Value;
                    Atomic("id:" + name, ImageVersions.IdFile(name), id.Groups["id"].Value,
                        () => Images[name] = Images[name] with { IdFile = id.Groups["id"].Value });
                    Writes.Add("ids:" + id.Groups["n"].Value);
                }

                if (catalog.Success)
                {
                    var name = catalog.Groups["n"].Value;
                    Atomic("catalog:" + name, ImagePaths.Image(name) + "/catalog.json", catalog.Groups["b64"].Value,
                        () => Images[name] = Images[name] with { CatalogBase64 = catalog.Groups["b64"].Value });
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
                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["b64"].Value));
                    Atomic("branch:" + name, BranchPaths.Json(name), json, () => Records[name] = json);
                    Writes.Add("branch:" + name);
                    return FakeContainerRuntime.Ok();
                }
            }

            return inner(container, command);
        };

        var run = World.Runtime.RunHandler;
        World.Runtime.RunHandler = spec =>
        {
            var credentialsWrite = spec.Stdin is not null && FakeContainerRuntime.ScriptOf(spec).Contains("credentials.json.new", StringComparison.Ordinal);
            if (credentialsWrite && !_inCredentialsHook && OnCredentialsWrite is { } hook)
            {
                _inCredentialsHook = true;
                try
                {
                    hook();
                }
                finally
                {
                    _inCredentialsHook = false;
                }
            }

            if (spec.Stdin is { } json && credentialsWrite && AfterStep is not null)
            {
                var state = json.Contains("\"state\":\"done\"", StringComparison.Ordinal) ? "done" : "running";
                var path = EngineCredentials.Path + ".new";
                if (TemporaryFiles.Remove(path))
                {
                    // The credentials write itself deletes a leftover first (D2): "rm -f" before the write.
                    DeletedFiles.Add(path);
                }
                TemporaryFiles[path] = json;
                foreach (var step in new[] { "write", "chown", "chmod", "sync" })
                {
                    AfterStep.Invoke("credentials:" + state + ":" + step);
                }
                var written = run(spec);
                TemporaryFiles.Remove(path);
                AfterStep.Invoke("credentials:" + state + ":rename");
                AfterStep.Invoke("credentials:" + state + ":directory-sync");
                Writes.Add("credentials:" + state);
                return written;
            }
            if (credentialsWrite && TemporaryFiles.Remove(EngineCredentials.Path + ".new"))
            {
                DeletedFiles.Add(EngineCredentials.Path + ".new");
            }
            var result = run(spec);
            if (spec.Stdin is { } content && FakeContainerRuntime.ScriptOf(spec).Contains("credentials.json.new", StringComparison.Ordinal))
            {
                TemporaryFiles.Remove(EngineCredentials.Path + ".new");
                Writes.Add(result.Succeeded
                    ? "credentials:" + (content.Contains("\"state\":\"done\"", StringComparison.Ordinal) ? "done" : "running")
                    : "credentials:failed");
            }

            return result;
        };
    }

    private static string Manifest(FakeImage image) => Convert.ToBase64String(Encoding.UTF8.GetBytes(new ImageManifest(
        ImageManifest.CurrentFormat, image.Name, "Shop", "16.0.4125.3",
        [new ImageFile("Shop", "Shop.mdf", "data", 8L << 20), new ImageFile("Shop_log", "Shop_log.ldf", "log", 524_288)],
        "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, image.BuiltAt,
        "shop.bak", new string('a', 64), new Dictionary<string, string>(), CliWorld.EngineImage).ToJson()));

    public Action<string>? AfterStep { get; set; }
    public Dictionary<string, string> TemporaryFiles { get; } = [];
    public List<string> DeletedFiles { get; } = [];

    private void Atomic(string label, string target, string content, Action commit)
    {
        TemporaryFiles[target + ".new"] = content;
        AfterStep?.Invoke(label + ":write");
        AfterStep?.Invoke(label + ":sync");
        commit();
        TemporaryFiles.Remove(target + ".new");
        AfterStep?.Invoke(label + ":rename");
        AfterStep?.Invoke(label + ":directory-sync");
    }

    public CliWorld World { get; }

    private bool _inCredentialsHook;

    /// <summary>Runs just before the upgrade writes a temporary credentials file, while the upgrade is paused in that write.</summary>
    public Action? OnCredentialsWrite { get; set; }

    /// <summary>An image folder as the enumeration sees it.</summary>
    public sealed record FakeImage(string Name, bool HasManifest, string? CatalogBase64, string? IdFile)
    {
        public DateTimeOffset BuiltAt { get; init; } = new(2026, 11, 16, 10, 30, 0, TimeSpan.Zero);
    }

    public Dictionary<string, FakeImage> Images { get; } = [];

    /// <summary>The per-name high-water mark files, which stay when an image is removed (design D4).</summary>
    public Dictionary<string, string> LastIds { get; } = [];

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

    public BranchRecord RecordOf(string name) => BranchRecord.FromJson(Records[name]);

    /// <summary>The catalog the upgrade wrote for an image, or null.</summary>
    public string? CatalogOf(string name) =>
        Images.GetValueOrDefault(name)?.CatalogBase64 is { } b64 ? Encoding.UTF8.GetString(Convert.FromBase64String(b64)) : null;

    public (int Exit, string Out, string Err) Run(params string[] args) => World.Run(args);

    [GeneratedRegex(@"printf %s '(?<id>[^']+)' > '/pool/images/\.ids/(?<n>[^']+)\.new'")]
    private static partial Regex IdWrite();

    [GeneratedRegex(@"printf %s '(?<b64>[A-Za-z0-9+/=]+)' \| base64 -d > '/pool/images/(?<n>[^/']+)/catalog\.json\.new'")]
    private static partial Regex CatalogWrite();
}

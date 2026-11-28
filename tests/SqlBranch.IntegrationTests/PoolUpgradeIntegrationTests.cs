using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 4.2 on the real engine, in whatever clone mode the pool has (thin on the managed XFS pool, copy on
/// <c>SQLBRANCH_IT_POOL_BACKEND=plain</c> over a pool without reflink): the upgrade of a format-1 pool with images and branches,
/// the status after it, the global options, and that it changes no data. The upgrade cannot be undone, so one test walks the
/// scenarios of the engine delta in order on the fixture's own engine.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PoolUpgradeIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    private BranchKit Kit => new(fixture, output);

    private JsonElement Json((int Exit, string Out, string Err, TimeSpan Elapsed) run, int exit = 0)
    {
        output.WriteLine(run.Out + run.Err);
        Assert.True(run.Exit == exit, run.Out + run.Err);
        return JsonDocument.Parse(run.Out).RootElement.Clone();
    }

    /// <summary>
    /// SHA-256 and modification time (with nanoseconds) of every image data file and manifest and of the offline branch's data files,
    /// and name and size of the open branch's data files.
    /// </summary>
    private Task<string> DataSnapshotAsync() => Kit.InPoolAsync(
        "{ find /pool/images -type f \\( -path '*/data/*' -o -name manifest.json \\) ; find /pool/branches/upold1/data -type f 2>/dev/null; } | sort | " +
        "while read -r f; do echo \"$(sha256sum \"$f\" | cut -d' ' -f1) $(stat -c %y \"$f\") $f\"; done; " +
        // The files of a branch whose database is open are written by the engine in the background (lazy writer, log), so their
        // content cannot be compared across a command. The names and sizes are: the upgrade must not add, remove or resize one.
        "find /pool/branches/updev1/data -type f 2>/dev/null | sort | while read -r f; do echo \"$(stat -c %s \"$f\") $f\"; done");

    /// <summary>
    /// Takes a branch database offline so the engine stops writing its files, or brings it back. The snapshot compares those files
    /// byte for byte, which an online database does not allow: the engine writes attached files in the background.
    /// </summary>
    private async Task SetOfflineAsync(string branch, bool offline)
    {
        await using var connection = await Kit.ConnectAsync("master");
        await BranchKit.ExecuteAsync(connection, offline
            ? $"ALTER DATABASE [{branch}] SET OFFLINE WITH ROLLBACK IMMEDIATE;"
            : $"ALTER DATABASE [{branch}] SET ONLINE;");
    }

    /// <summary>
    /// The snapshot once two reads three seconds apart agree. A running engine keeps writing the log of an attached database for a
    /// short while after a checkpoint, with no change of modification time on a bind mount, so the upgrade is compared with a
    /// quiet pool, not with one the engine is still writing.
    /// </summary>
    private async Task<string> QuietSnapshotAsync()
    {
        var last = await DataSnapshotAsync();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var next = await DataSnapshotAsync();
            if (next == last)
            {
                return next;
            }

            last = next;
        }

        Assert.Fail("The pool did not become quiet.");
        return last;
    }

    /// <summary>The records of the branches, for checking that a completed rerun leaves them unchanged.</summary>
    private Task<string> BranchRecordsAsync() => Kit.InPoolAsync(
        "for f in /pool/branches/*/branch.json; do echo \"$(sha256sum \"$f\" | cut -d' ' -f1) $(stat -c %y \"$f\") $f\"; done");

    private async Task<string> LoginsAsync()
    {
        await using var connection = await Kit.ConnectAsync("master");
        await using var command = new SqlCommand("SELECT name FROM sys.server_principals WHERE type IN ('S', 'U', 'G') ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return string.Join(',', names);
    }

    private static async Task<string> StartedAtAsync(EngineWorld world)
    {
        var inspected = await EngineWorld.DockerAsync("inspect", "-f", "{{.State.StartedAt}}", world.EngineName);
        Assert.Equal(0, inspected.ExitCode);
        return inspected.StdOut.Trim();
    }

    [IntegrationFact]
    public async Task The_enumeration_script_lists_images_and_branch_folders_and_skips_dot_folders_and_build_staging()
    {
        var script = SqlBranch.Core.Pools.PoolUpgrader.EnumerationScript;
        const string Setup =
            "mkdir -p /pool/images/enumx /pool/images/enumx-seal /pool/images/.removing-enumx /pool/images/.ids /pool/build/enumxid /pool/branches/enumxb /pool/branches/.enumx-dot && " +
            "echo '{}' > /pool/images/enumx/manifest.json && echo '{}' > /pool/images/.removing-enumx/manifest.json && " +
            "printf %s enumx@20261201T090000000Z > /pool/images/.ids/enumx && printf %s enumx > /pool/images/.ids/enumx-seal";
        const string Cleanup =
            "rm -rf /pool/images/enumx /pool/images/enumx-seal /pool/images/.removing-enumx /pool/images/.ids/enumx /pool/images/.ids/enumx-seal /pool/build/enumxid /pool/branches/enumxb /pool/branches/.enumx-dot";
        await Kit.InPoolAsync(Setup);
        try
        {
            var lines = (await Kit.InPoolAsync(script)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains("enumx", StringComparison.Ordinal)).ToArray();
            Assert.Equal(["SQLBRANCH_UPIMAGE|enumx|1|none|enumx@20261201T090000000Z", "SQLBRANCH_UPBRANCH|enumxb"], lines);
        }
        finally
        {
            await Kit.InPoolAsync(Cleanup);
        }
    }

    [IntegrationFormat1Fact]
    public async Task A_format_1_pool_with_images_and_branches_upgrades_without_touching_data_and_reports_format_2()
    {
        await Kit.EnsureImageAsync("upshop");
        await Kit.EnsureImageAsync("upcrm");
        await Kit.CreateAsync("updev1", "upshop");
        await Kit.CreateAsync("upold1", "upshop");
        await using var open = await Kit.ConnectAsync("updev1");
        await BranchKit.ExecuteAsync(open, "INSERT dbo.Item (Id, Name) VALUES (5000, N'extra row'); CHECKPOINT;");
        await using (var other = await Kit.ConnectAsync("upold1"))
        {
            await BranchKit.ExecuteAsync(other, "CHECKPOINT;");
        }

        output.WriteLine("clone mode: " + await Kit.CloneModeAsync());
        // upold1 is offline for the snapshots, so its files are compared byte for byte; updev1 stays open (its connection must
        // survive the upgrade), so only its file names and sizes are compared. Images are compared byte for byte.
        await SetOfflineAsync("upold1", offline: true);
        Assert.Equal(1, Json(World.RunCli("status", "--format", "json")).GetProperty("poolFormat").GetInt32());
        var before = await QuietSnapshotAsync();
        var loginsBefore = await LoginsAsync();
        var startedBefore = await StartedAtAsync(World);
        Assert.Contains("/data/", before, StringComparison.Ordinal);

        // "Upgrade a pool with images and branches"
        var upgraded = Json(World.RunCli("pool", "upgrade", "--yes", "--format", "json"));

        // "Data untouched by the upgrade": taken right after the command, before anything else touches the live branch files.
        var afterUpgrade = await DataSnapshotAsync();
        Assert.Equal(before, afterUpgrade);
        await SetOfflineAsync("upold1", offline: false);
        Assert.Equal(2, upgraded.GetProperty("pool").GetProperty("format").GetInt32());
        Assert.Equal(1, upgraded.GetProperty("pool").GetProperty("previousFormat").GetInt32());
        var versions = upgraded.GetProperty("images").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i.GetProperty("version").GetString()!);
        Assert.StartsWith("upcrm@", versions["upcrm"], StringComparison.Ordinal);
        Assert.StartsWith("upshop@", versions["upshop"], StringComparison.Ordinal);
        Assert.Matches(@"^upshop@\d{8}T\d{9}Z$", versions["upshop"]);
        foreach (var name in new[] { "updev1", "upold1" })
        {
            var record = JsonDocument.Parse(await Kit.InPoolAsync($"cat /pool/branches/{name}/branch.json")).RootElement;
            Assert.True(record.GetProperty("baseKnown").GetBoolean());
            Assert.Equal(versions["upshop"], record.GetProperty("imageVersion").GetString());
            Assert.Contains(name, upgraded.GetProperty("branches").GetProperty("pinned").EnumerateArray().Select(b => b.GetString()));
        }
        var recordsBefore = await BranchRecordsAsync();

        var listed = Json(World.RunCli("image", "list", "--format", "json")).GetProperty("images").EnumerateArray()
            .ToDictionary(i => i.GetProperty("name").GetString()!, i => i.GetProperty("version").GetString());
        Assert.Equal(versions["upshop"], listed["upshop"]);
        Assert.Equal(versions["upcrm"], listed["upcrm"]);
        await using (var again = await Kit.ConnectAsync("updev1"))
        {
            // The image has 100 rows and dev1 holds one extra.
            Assert.Equal(101, await BranchKit.CountAsync(again, "SELECT COUNT(*) FROM dbo.Item"));
        }

        // The rewritten credentials file keeps the engine user as owner and mode 600.
        Assert.Equal("600 10001", await Kit.InPoolAsync("stat -c '%a %u' /pool/engine/credentials.json"));

        // "Status after the upgrade"
        var status = World.RunCli("status", "--format", "json");
        Assert.Equal(0, status.Exit);
        Assert.Equal(2, JsonDocument.Parse(status.Out).RootElement.GetProperty("poolFormat").GetInt32());
        Assert.Empty(JsonDocument.Parse(status.Out).RootElement.GetProperty("mismatches").EnumerateArray());
        var text = World.RunCli("status");
        Assert.Equal(0, text.Exit);
        Assert.Contains("Pool format: 2", text.Out, StringComparison.Ordinal);

        Assert.Equal(loginsBefore, await LoginsAsync());
        Assert.Equal(startedBefore, await StartedAtAsync(World));

        // "Upgrade twice" and "Global options are accepted": the rerun is a format-2 to format-2 no-op.
        var config = Path.Combine(fixture.ClientFolder, "ci.sqlbranch.yml");
        await File.WriteAllTextAsync(config, "requireThin: false\n");
        // upold1 was brought online and offline again since the first snapshot (the engine writes its files on startup and when it
        // goes offline), so the rerun is compared with a fresh quiet snapshot. upold1 stays offline until after the comparison.
        await SetOfflineAsync("upold1", offline: true);
        var beforeRerun = await QuietSnapshotAsync();
        var rerun = Json(World.RunCli("pool", "upgrade", "--yes", "--quiet", "--config", config, "--format", "json"));
        Assert.Equal(2, rerun.GetProperty("pool").GetProperty("format").GetInt32());
        Assert.Equal(2, rerun.GetProperty("pool").GetProperty("previousFormat").GetInt32());
        Assert.Empty(rerun.GetProperty("images").EnumerateArray());
        var rerunText = World.RunCli("pool", "upgrade", "--yes");
        Assert.Equal(0, rerunText.Exit);
        Assert.Contains("already uses format 2", rerunText.Out, StringComparison.Ordinal);

        // ...and after the rerun, against the quiet snapshot taken just before it.
        Assert.Equal(beforeRerun, await DataSnapshotAsync());
        await SetOfflineAsync("upold1", offline: false);
        Assert.Equal(recordsBefore, await BranchRecordsAsync());
        Assert.Equal(loginsBefore, await LoginsAsync());
        Assert.Equal(startedBefore, await StartedAtAsync(World));

        // The open connection of dev1 can still commit, and a branch can be made on the upgraded pool.
        await BranchKit.ExecuteAsync(open, "INSERT dbo.Item (Id, Name) VALUES (5001, N'after the upgrade');");
        Assert.Equal(2, await BranchKit.CountAsync(open, "SELECT COUNT(*) FROM dbo.Item WHERE Id >= 5000"));
        Json(World.RunCli("branch", "create", "upnew", "--from", "upshop", "--format", "json"));

        await Kit.DropBranchAsync("upnew");
        await Kit.DropBranchAsync("updev1");
        await Kit.DropBranchAsync("upold1");
    }
}

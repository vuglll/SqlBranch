using System.Globalization;
using System.Text.Json;

namespace SqlBranch.IntegrationTests;

/// <summary>Tasks 5.11 to 5.14 on the real engine: <c>image list</c>, <c>image rm</c> and the rebuilt log.</summary>
public sealed partial class ImageBuildIntegrationTests
{
    private static string ErrorCodeIn(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>
    /// A branch folder with only a <c>branch.json</c>, as far as <c>image list</c> and <c>image rm</c> can tell. There is no
    /// <c>branch create</c> yet; the test removes the folder itself. A real branch's files are never made or deleted here.
    /// </summary>
    private async Task MakeFakeBranchFolderAsync(string branch, string image) =>
        await InPoolAsync($"mkdir -p /pool/branches/{branch} && printf '%s' '{{\"image\":\"{image}\",\"cloneMode\":\"thin\"}}' > /pool/branches/{branch}/branch.json");

    private async Task RemoveFakeBranchFolderAsync(string branch) =>
        await InPoolAsync($"rm -rf /pool/branches/{branch}");

    // ---- 5.11: image list ----

    [IntegrationFact]
    public async Task Image_list_shows_name_source_size_build_time_and_the_number_of_branches_in_text_and_json()
    {
        var bak = await fixture.MakeBackupAsync("Listed");
        var built = World.RunCli("image", "build", "listed", "--from-bak", bak, "--format", "json");
        Assert.True(built.Exit == 0, built.Out + built.Err);

        var json = World.RunCli("image", "list", "--format", "json");
        var text = World.RunCli("image", "list");
        output.WriteLine(text.Out);

        Assert.Equal(0, json.Exit);
        var listed = JsonDocument.Parse(json.Out).RootElement.GetProperty("images").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "listed");
        Assert.Equal("Listed", listed.GetProperty("sourceDatabase").GetString());
        Assert.True(listed.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.Equal(0, listed.GetProperty("branches").GetInt32());
        Assert.True(DateTimeOffset.TryParse(listed.GetProperty("builtAt").GetString(), CultureInfo.InvariantCulture, out var builtAt));
        Assert.True(DateTimeOffset.UtcNow - builtAt < TimeSpan.FromHours(1));
        Assert.Equal(2, listed.GetProperty("files").GetArrayLength());

        Assert.Equal(0, text.Exit);
        var line = text.Out.Split('\n').Single(l => l.StartsWith("listed", StringComparison.Ordinal));
        Assert.Contains("Listed", line, StringComparison.Ordinal);
        // Format 2 adds a VERSION column after BRANCHES.
        var branchesCell = listed.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
            ? line.TrimEnd()[..^version.GetString()!.Length].TrimEnd()
            : line.TrimEnd();
        Assert.EndsWith("0", branchesCell, StringComparison.Ordinal);
        Assert.Contains("BRANCHES", text.Out, StringComparison.Ordinal);

        // A branch folder that names the image is counted; one that names another image is not. A folder being removed is not an image.
        await MakeFakeBranchFolderAsync("it-list-a", "listed");
        await MakeFakeBranchFolderAsync("it-list-b", "other-image");
        await InPoolAsync("mkdir -p /pool/images/.removing-ghost && cp /pool/images/listed/manifest.json /pool/images/.removing-ghost/manifest.json");
        try
        {
            var again = JsonDocument.Parse(World.RunCli("image", "list", "--format", "json").Out).RootElement.GetProperty("images").EnumerateArray().ToList();
            var counted = again.Single(i => i.GetProperty("name").GetString() == "listed");
            Assert.Equal(1, counted.GetProperty("branches").GetInt32());
            Assert.Equal(["it-list-a"], counted.GetProperty("branchNames").EnumerateArray().Select(b => b.GetString()!).ToArray());
            Assert.DoesNotContain(again, i => i.GetProperty("name").GetString()!.Contains("ghost", StringComparison.Ordinal));
        }
        finally
        {
            await RemoveFakeBranchFolderAsync("it-list-a");
            await RemoveFakeBranchFolderAsync("it-list-b");
            await InPoolAsync("rm -rf /pool/images/.removing-ghost");
        }
    }

    // ---- 5.12: image rm ----

    [IntegrationFact]
    public async Task Rm_of_an_unused_image_deletes_it_and_leaves_nothing_behind_and_rm_of_an_unknown_name_exits_1()
    {
        var bak = await fixture.MakeBackupAsync("Doomed2");
        Assert.True(World.RunCli("image", "build", "removable", "--from-bak", bak).Exit == 0);
        Assert.Contains("removable", await InPoolAsync("ls /pool/images"), StringComparison.Ordinal);

        var run = World.RunCli("image", "rm", "removable", "--format", "json");
        output.WriteLine(run.Out + run.Err);

        Assert.Equal(0, run.Exit);
        Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/images | grep removable"));
        Assert.DoesNotContain("removable", World.RunCli("image", "list").Out, StringComparison.Ordinal);
        Assert.Equal(0, World.RunCli("status").Exit);

        var again = World.RunCli("image", "rm", "removable", "--format", "json");
        Assert.Equal(1, again.Exit);
        Assert.Equal("image_not_found", ErrorCodeIn(again.Out));
    }

    [IntegrationFact]
    public async Task Rm_with_a_name_that_is_a_path_exits_2_and_changes_nothing_in_the_pool()
    {
        var before = await PoolSnapshotAsync();

        var run = World.RunCli("image", "rm", "../branches/pr-142", "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Equal("invalid_name", ErrorCodeIn(run.Out));
        Assert.Equal(before, await PoolSnapshotAsync());
    }

    // ---- 5.13 (the part that needs no branch create) ----

    [IntegrationFact]
    public async Task Rm_of_an_image_with_branches_needs_force_and_never_touches_the_branch_folder()
    {
        var bak = await fixture.MakeBackupAsync("InUse");
        Assert.True(World.RunCli("image", "build", "in-use", "--from-bak", bak).Exit == 0);
        await MakeFakeBranchFolderAsync("it-use-1", "in-use");
        try
        {
            var branchFiles = await InPoolAsync("cd /pool/branches/it-use-1 && find . -type f | sort | xargs sha256sum");
            var snapshot = await PoolSnapshotAsync();

            var refused = World.RunCli("image", "rm", "in-use", "--format", "json");
            output.WriteLine(refused.Out);

            Assert.Equal(1, refused.Exit);
            Assert.Equal("image_in_use", ErrorCodeIn(refused.Out));
            Assert.Contains("it-use-1", refused.Out, StringComparison.Ordinal);
            Assert.Equal(snapshot, await PoolSnapshotAsync());

            var forced = World.RunCli("image", "rm", "in-use", "--force", "--format", "json");
            output.WriteLine(forced.Out);

            Assert.Equal(0, forced.Exit);
            Assert.DoesNotContain("in-use", await InPoolAsync("ls -A /pool/images"), StringComparison.Ordinal);
            Assert.Equal(branchFiles, await InPoolAsync("cd /pool/branches/it-use-1 && find . -type f | sort | xargs sha256sum"));
        }
        finally
        {
            await RemoveFakeBranchFolderAsync("it-use-1");
        }
    }

    // ---- 5.14: the log of an image is rebuilt ----

    [IntegrationFact]
    public async Task A_source_with_a_256_MB_log_gives_an_image_log_under_8_MiB_and_the_manifest_records_the_original_size()
    {
        const long LogBytes = 256L * 1024 * 1024;
        await fixture.SqlAsync(
            "CREATE DATABASE [BigLog]",
            "ALTER DATABASE [BigLog] MODIFY FILE (NAME = BigLog_log, SIZE = 256MB)",
            "EXEC [BigLog].sys.sp_executesql N'CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY); INSERT dbo.T VALUES (1), (2), (3);'",
            "BACKUP DATABASE [BigLog] TO DISK = N'/tmp/biglog.bak' WITH INIT");
        Assert.Equal(LogBytes, Convert.ToInt64(await fixture.ScalarAsync("SELECT CAST(size AS bigint) * 8192 FROM sys.master_files WHERE database_id = DB_ID(N'BigLog') AND type = 1"), CultureInfo.InvariantCulture));
        await fixture.SqlAsync("DROP DATABASE [BigLog]");
        var bak = await fixture.TakeFromEngineAsync("biglog.bak");

        var run = World.RunCli("image", "build", "biglog", "--from-bak", bak, "--format", "json");
        output.WriteLine(run.Out + run.Err);

        Assert.True(run.Exit == 0, run.Out + run.Err);
        var manifest = JsonDocument.Parse(await InPoolAsync("cat /pool/images/biglog/manifest.json")).RootElement;
        Assert.Equal(LogBytes, manifest.GetProperty("originalLogSizeBytes").GetInt64());
        Assert.True(manifest.GetProperty("logRebuilt").GetBoolean());

        var log = manifest.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("type").GetString() == "log");
        Assert.True(log.GetProperty("sizeBytes").GetInt64() < 8L * 1024 * 1024);
        // The file itself, not only what the manifest says.
        var onDisk = long.Parse(await InPoolAsync($"stat -c %s /pool/images/biglog/data/{log.GetProperty("fileName").GetString()}"), CultureInfo.InvariantCulture);
        output.WriteLine($"log on disk: {onDisk} bytes");
        Assert.True(onDisk < 8L * 1024 * 1024);

        // The listing reports the small image, not the 256 MB the source had.
        var listed = JsonDocument.Parse(World.RunCli("image", "list", "--format", "json").Out).RootElement.GetProperty("images").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "biglog");
        Assert.True(listed.GetProperty("sizeBytes").GetInt64() < 64L * 1024 * 1024);
        Assert.Equal(0, World.RunCli("status").Exit);
    }
}

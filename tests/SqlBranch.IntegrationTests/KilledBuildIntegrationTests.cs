using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// A build whose client dies. It has an engine of its own (its own fixture), because it makes a big database
/// and leaves a temporary database and a staging folder behind until the test removes them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KilledBuildIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private EngineWorld World => fixture.World!;

    private async Task<string> InPoolAsync(string script) =>
        (await World.Runtime.ExecAsync(World.EngineName, ["sh", "-c", script], CancellationToken.None)).StdOut.Trim();

    private async Task<string> BuildDatabasesAsync() =>
        Convert.ToString(await fixture.ScalarAsync("SELECT TOP (1) CONVERT(nvarchar(200), name) COLLATE DATABASE_DEFAULT + N':' + CONVERT(nvarchar(60), state_desc) COLLATE DATABASE_DEFAULT FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'"), CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A database with a data file big enough that its restore takes a while, backed up and dropped.</summary>
    private async Task<string> MakeBigBackupAsync()
    {
        await fixture.SqlAsync(
            "CREATE DATABASE [Big]",
            "ALTER DATABASE [Big] MODIFY FILE (NAME = N'Big', SIZE = 700MB)",
            "EXEC [Big].sys.sp_executesql N'CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, Pad char(1000) NOT NULL); INSERT dbo.T SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), ''x'' FROM sys.all_objects a CROSS JOIN sys.all_objects b'",
            "BACKUP DATABASE [Big] TO DISK = N'/tmp/big.bak' WITH INIT");
        var bak = await fixture.TakeFromEngineAsync("big.bak");
        await fixture.SqlAsync("DROP DATABASE [Big]");
        return bak;
    }

    /// <summary>Waits until a temporary database of a build is in the state <paramref name="state"/> (any state when null).</summary>
    private async Task WaitForBuildDatabaseAsync(CliProcess build, string? state)
    {
        var clock = Stopwatch.StartNew();
        string databases;
        do
        {
            Assert.False(build.HasExited, "The build ended before the database reached the state " + state);
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(4), "The database did not reach the state " + state);
            await Task.Delay(100);
            databases = await BuildDatabasesAsync();
        }
        while (databases.Length == 0 || (state is not null && !databases.EndsWith(":" + state, StringComparison.Ordinal)));

        output.WriteLine($"seen after {clock.Elapsed.TotalSeconds:F1} s: {databases}");
    }

    /// <summary>With the client alive and holding its locks, status calls the build in progress and exits 0.</summary>
    private void AssertStatusSeesARunningBuild()
    {
        var running = World.RunCli("status", "--format", "json");
        output.WriteLine(running.Out);
        Assert.Equal(0, running.Exit);
        var root = JsonDocument.Parse(running.Out).RootElement;
        Assert.Contains(root.GetProperty("inProgress").EnumerateArray().Select(e => e.GetString()), p => p!.StartsWith("image build ", StringComparison.Ordinal));
        Assert.Empty(root.GetProperty("mismatches").EnumerateArray());
    }

    /// <summary>Kills the client, then waits for the engine to notice (the locks go with the session) and returns what status then says.</summary>
    private async Task<(JsonElement Root, List<JsonElement> Mismatches)> KillAndReadStatusAsync(CliProcess build)
    {
        build.Kill();
        var clock = Stopwatch.StartNew();
        (int Exit, string Out) after;
        do
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(6), "status never reported the dead build.");
            await Task.Delay(1000);
            var run = World.RunCli("status", "--format", "json");
            after = (run.Exit, run.Out);
        }
        while (after.Exit == 0);

        output.WriteLine($"status after {clock.Elapsed.TotalSeconds:F1} s: {after.Out}");
        output.WriteLine("databases now: " + await BuildDatabasesAsync());
        Assert.Equal(1, after.Exit);
        var root = JsonDocument.Parse(after.Out).RootElement.Clone();
        Assert.Empty(root.GetProperty("inProgress").EnumerateArray());
        return (root, [.. root.GetProperty("mismatches").EnumerateArray()]);
    }

    /// <summary>There is no doctor --fix in 0.1: remove what the dead build left, as status says to.</summary>
    private async Task RemoveLeftoversAsync()
    {
        await fixture.SqlAsync("DECLARE @n sysname, @sql nvarchar(400); DECLARE c CURSOR FOR SELECT name FROM sys.databases WHERE name LIKE 'sqlbranch[_]build[_]%'; OPEN c; FETCH NEXT FROM c INTO @n; " +
            "WHILE @@FETCH_STATUS = 0 BEGIN SET @sql = N'DROP DATABASE ' + QUOTENAME(@n); EXEC (@sql); FETCH NEXT FROM c INTO @n; END; CLOSE c; DEALLOCATE c;");
        await InPoolAsync("rm -rf /pool/build/* /pool/images/killed");
        Assert.Equal(string.Empty, await BuildDatabasesAsync());
        Assert.Equal(0, World.RunCli("status").Exit);
    }

    [IntegrationFact]
    public async Task A_build_killed_during_the_restore_is_listed_by_status_and_a_running_build_is_not()
    {
        var bak = await MakeBigBackupAsync();
        output.WriteLine($"backup: {new FileInfo(bak).Length} bytes");
        using var build = World.StartCli("image", "build", "killed", "--from-bak", bak, "--format", "json");
        try
        {
            await WaitForBuildDatabaseAsync(build, "RESTORING");
            AssertStatusSeesARunningBuild();

            var (_, mismatches) = await KillAndReadStatusAsync(build);

            // The staging folder is always left. The engine drops a half-restored database when its client goes away
            // during the RESTORE statement, so that case leaves no database; whatever database is there is listed.
            var folder = Assert.Single(mismatches, m => m.GetProperty("kind").GetString() == "build_folder");
            Assert.StartsWith("/pool/build/", folder.GetProperty("name").GetString(), StringComparison.Ordinal);
            var present = await BuildDatabasesAsync();
            var listed = mismatches.Where(m => m.GetProperty("kind").GetString() == "build_database").ToList();
            Assert.Equal(present.Length > 0, listed.Count == 1);
            Assert.Equal(mismatches.Count, 1 + listed.Count);
            // No image: a manifest is what makes one. (A kill that lands after the seal started leaves an empty
            // image folder; see docs/feature-drafts.md.)
            Assert.Equal(string.Empty, await InPoolAsync("ls /pool/images/*/manifest.json 2>/dev/null"));
        }
        finally
        {
            build.Dispose();
            await RemoveLeftoversAsync();
        }
    }
}

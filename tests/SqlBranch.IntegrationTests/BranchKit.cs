using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlBranch.Core.Engine;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// The helpers and the clone-mode-independent scenarios that tasks 5.13, 6.7 and 6.8 run in the thin pool and in a
/// copy-mode pool. A scenario is written once here and called from the class of each pool, so both modes get the same checks.
/// Everything happens on the fixture's own engine; branches and images a scenario makes are removed in its <c>finally</c>.
/// </summary>
internal sealed class BranchKit(ImageEngineFixture fixture, ITestOutputHelper output)
{
    private const string Source = "Shop";

    public EngineWorld World => fixture.World!;

    public async Task<string> InPoolAsync(string script) =>
        (await World.Runtime.ExecAsync(World.EngineName, ["sh", "-c", script], CancellationToken.None)).StdOut.Trim();

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string ErrorCode(string json) => Parse(json).GetProperty("error").GetProperty("code").GetString()!;

    public static string ErrorMessage(string json) => Parse(json).GetProperty("error").GetProperty("message").GetString()!;

    public async Task<string> CloneModeAsync() =>
        Parse(World.RunCli("status", "--format", "json").Out).GetProperty("pool").GetProperty("cloneMode").GetString()!;

    /// <summary>Builds image <paramref name="image"/> from a backup of database Shop (100 rows in <c>dbo.Item</c>) unless it exists.</summary>
    public async Task EnsureImageAsync(string image)
    {
        if ((await InPoolAsync($"ls /pool/images/{image}/manifest.json 2>/dev/null")).Length > 0)
        {
            return;
        }

        var bak = Path.Combine(fixture.ClientFolder, Source + ".bak");
        if (!File.Exists(bak))
        {
            bak = await fixture.MakeBackupAsync(Source);
        }

        var run = World.RunCli("image", "build", image, "--from-bak", bak, "--format", "json");
        Assert.True(run.Exit == 0, run.Out + run.Err);
    }

    public async Task<JsonElement> CreateAsync(string branch, string image, params string[] more)
    {
        var run = World.RunCli(["branch", "create", branch, "--from", image, .. more, "--format", "json"]);
        output.WriteLine(run.Out + run.Err);
        Assert.True(run.Exit == 0, run.Out + run.Err);
        return Parse(run.Out).GetProperty("branch");
    }

    /// <summary>
    /// Cleanup of a branch a test made: <c>branch rm</c>, which is what a user runs. A branch that is not there (exit 1
    /// <c>branch_not_found</c>) is fine. Anything else is logged and followed by the raw cleanup on this world's own
    /// engine, so a failed <c>rm</c> never leaves a branch behind.
    /// </summary>
    public async Task DropBranchAsync(string name)
    {
        var removed = World.RunCli("branch", "rm", name, "--format", "json");
        if (removed.Exit != 0 && !(removed.Exit == 1 && removed.Out.Contains("\"branch_not_found\"", StringComparison.Ordinal)))
        {
            output.WriteLine($"cleanup: branch rm {name} exited {removed.Exit}: {removed.Out}{removed.Err}");
        }

        await fixture.SqlAsync(
            $"IF DB_ID(N'{name}') IS NOT NULL ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
            $"IF DB_ID(N'{name}') IS NOT NULL DROP DATABASE [{name}]");
        await InPoolAsync($"rm -rf /pool/branches/{name}");
    }

    public async Task RemoveImageFilesAsync(string image) => await InPoolAsync($"rm -rf /pool/images/{image}");

    public void AssertStatusIsClean()
    {
        var status = World.RunCli("status", "--format", "json");
        output.WriteLine(status.Out);
        Assert.Equal(0, status.Exit);
        Assert.Empty(Parse(status.Out).GetProperty("mismatches").EnumerateArray());
    }

    /// <summary>A connection to <paramref name="database"/> with the password in the pool's credentials file.</summary>
    public async Task<SqlConnection> ConnectAsync(string database)
    {
        var pool = await World.FindPoolAsync();
        var password = await EngineCredentials.ReadAsync(World.Runtime, pool.Engine!.Image, pool.Location, CancellationToken.None);
        Assert.NotNull(password);
        var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{World.Port.ToString(CultureInfo.InvariantCulture)}",
            InitialCatalog = database,
            UserID = "sa",
            Password = password,
            TrustServerCertificate = true,
            Pooling = false,
            CommandTimeout = 600,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task<long> CountAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The SHA-256 of every file of an image, as <c>sha256sum</c> prints them.</summary>
    public Task<string> ImageHashesAsync(string image) => InPoolAsync($"cd /pool/images/{image}/data && sha256sum * | sort -k2");

    private JsonElement ListedBranch(string name)
    {
        var list = World.RunCli("branch", "list", "--format", "json");
        Assert.True(list.Exit == 0, list.Out + list.Err);
        return Parse(list.Out).GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);
    }

    // ---- 5.13: an image in use ----

    /// <summary>
    /// A branch from an image: <c>image rm</c> exits 1 <c>image_in_use</c> and lists it, <c>image rm --force</c> exits 0, and the
    /// branch still answers queries and takes writes. Runs in whatever clone mode the pool has.
    /// </summary>
    public async Task ForcedImageRemovalLeavesTheBranchWorkingAsync()
    {
        await EnsureImageAsync("inuse");
        try
        {
            var branch = await CreateAsync("pr-use", "inuse");
            output.WriteLine("clone mode: " + branch.GetProperty("cloneMode").GetString());

            var refused = World.RunCli("image", "rm", "inuse", "--format", "json");
            output.WriteLine(refused.Out);
            Assert.Equal(1, refused.Exit);
            Assert.Equal("image_in_use", ErrorCode(refused.Out));
            Assert.Contains("pr-use", ErrorMessage(refused.Out), StringComparison.Ordinal);
            Assert.Equal("1", await InPoolAsync("ls /pool/images/inuse/manifest.json | wc -l"));

            var forced = World.RunCli("image", "rm", "inuse", "--force", "--format", "json");
            output.WriteLine(forced.Out);
            Assert.Equal(0, forced.Exit);
            Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/images | grep -x inuse"));

            // A query through a new connection, and a write that has to go to blocks the branch no longer shares with anything.
            await using var connection = await ConnectAsync("pr-use");
            Assert.Equal(100, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            await ExecuteAsync(connection, "INSERT dbo.Item (Id, Name) VALUES (1000, N'after the image is gone'); CHECKPOINT;");
            Assert.Equal(101, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("pr-use");
            await RemoveImageFilesAsync("inuse");
        }
    }

    // ---- 6.4: thin branches are cheap ----

    private async Task<long> PoolUsedBytesAsync() =>
        long.Parse(await InPoolAsync("sync; df -B1 --output=used /pool | tail -n 1 | tr -d ' '"), CultureInfo.InvariantCulture);

    /// <summary>
    /// Three branches from an image of 1 GiB and more grow the pool's used space by under 48 MiB. Thin mode only: in copy mode the
    /// test is reported as skipped, not as passed, before it builds anything.
    /// </summary>
    public async Task ThreeBranchesOfALargeImageGrowThePoolLittleAsync()
    {
        SkipTestException.UnlessThin(await CloneModeAsync());
        const long OneGiB = 1L << 30;

        // About 1.1 GiB of data pages: 140,000 rows of one page each.
        var bak = await fixture.MakeLargeBackupAsync("Big", 140_000);
        var build = World.RunCli("image", "build", "big", "--from-bak", bak, "--format", "json");
        output.WriteLine(build.Out + build.Err);
        output.WriteLine($"image build: {build.Elapsed.TotalSeconds:F0} s");
        Assert.True(build.Exit == 0, build.Out + build.Err);
        var imageBytes = Parse(build.Out).GetProperty("image").GetProperty("sizeBytes").GetInt64();
        Assert.True(imageBytes >= OneGiB, $"the image is {imageBytes} bytes, under 1 GiB");

        try
        {
            var before = await PoolUsedBytesAsync();
            foreach (var name in new[] { "big-1", "big-2", "big-3" })
            {
                var run = World.RunCli("branch", "create", name, "--from", "big", "--format", "json");
                output.WriteLine($"{name}: {run.Elapsed.TotalSeconds:F1} s");
                Assert.True(run.Exit == 0, run.Out + run.Err);
                Assert.Equal("thin", Parse(run.Out).GetProperty("branch").GetProperty("cloneMode").GetString());
            }

            var growth = (await PoolUsedBytesAsync()) - before;
            output.WriteLine($"pool used space grew by {growth} bytes ({growth / 1048576.0:F1} MiB) for three branches of a {imageBytes / 1048576.0:F0} MiB image");
            Assert.True(growth < 48L * 1024 * 1024, $"the pool grew by {growth} bytes");

            // The branches are whole databases, not only files.
            await using var connection = await ConnectAsync("big-3");
            Assert.Equal(140_000, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("big-1");
            await DropBranchAsync("big-2");
            await DropBranchAsync("big-3");
            await RemoveImageFilesAsync("big");
        }
    }

    // ---- 6.7: isolation ----

    /// <summary>
    /// A row written in one branch is absent from a sibling and from a branch created afterwards, and the image's files
    /// have the same SHA-256 values before and after.
    /// </summary>
    public async Task BranchesAreIsolatedAsync()
    {
        await EnsureImageAsync("shop");
        var hashes = await ImageHashesAsync("shop");
        output.WriteLine(hashes);
        Assert.Equal(2, hashes.Split('\n').Length);
        try
        {
            await CreateAsync("iso-a", "shop");
            await CreateAsync("iso-b", "shop");

            await using (var a = await ConnectAsync("iso-a"))
            {
                await ExecuteAsync(a, "INSERT dbo.Item (Id, Name) VALUES (7001, N'only in a'); UPDATE dbo.Item SET Name = N'changed in a' WHERE Id = 1; CHECKPOINT;");
                Assert.Equal(101, await CountAsync(a, "SELECT COUNT(*) FROM dbo.Item"));
                Assert.Equal(1, await CountAsync(a, "SELECT COUNT(*) FROM dbo.Item WHERE Id = 7001"));
            }

            await using (var b = await ConnectAsync("iso-b"))
            {
                Assert.Equal(100, await CountAsync(b, "SELECT COUNT(*) FROM dbo.Item"));
                Assert.Equal(0, await CountAsync(b, "SELECT COUNT(*) FROM dbo.Item WHERE Id = 7001"));
                Assert.Equal(0, await CountAsync(b, "SELECT COUNT(*) FROM dbo.Item WHERE Name = N'changed in a'"));
            }

            await CreateAsync("iso-c", "shop");
            await using (var c = await ConnectAsync("iso-c"))
            {
                Assert.Equal(100, await CountAsync(c, "SELECT COUNT(*) FROM dbo.Item"));
                Assert.Equal(0, await CountAsync(c, "SELECT COUNT(*) FROM dbo.Item WHERE Id = 7001"));
                Assert.Equal(0, await CountAsync(c, "SELECT COUNT(*) FROM dbo.Item WHERE Name = N'changed in a'"));
            }

            // The image itself did not change: not a byte of it, and still read-only.
            Assert.Equal(hashes, await ImageHashesAsync("shop"));
            Assert.All((await InPoolAsync("stat -c '%a' /pool/images/shop/data/*")).Split('\n'), mode => Assert.Equal("444", mode));
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("iso-a");
            await DropBranchAsync("iso-b");
            await DropBranchAsync("iso-c");
        }
    }

    // ---- 6.8: branch list ----

    /// <summary>
    /// <c>branch list</c> in the pool's clone mode: columns, own changed-data size before and after a write, offline and broken
    /// states, and a branch whose image was removed with <c>--force</c>.
    /// </summary>
    public async Task BranchListShowsStateOwnSizeAndARemovedImageAsync()
    {
        await EnsureImageAsync("listimg");
        var mode = await CloneModeAsync();
        var manifest = Parse(await InPoolAsync("cat /pool/images/listimg/manifest.json"));
        var imageBytes = manifest.GetProperty("files").EnumerateArray().Sum(f => f.GetProperty("sizeBytes").GetInt64());
        output.WriteLine($"mode {mode}, image {imageBytes} bytes");
        try
        {
            await CreateAsync("pr-142", "listimg");

            var first = ListedBranch("pr-142");
            output.WriteLine(first.ToString());
            Assert.Equal("listimg", first.GetProperty("image").GetString());
            Assert.False(first.GetProperty("imageRemoved").GetBoolean());
            Assert.Equal(mode, first.GetProperty("cloneMode").GetString());
            Assert.Equal("online", first.GetProperty("state").GetString());
            Assert.True(DateTimeOffset.UtcNow - DateTimeOffset.Parse(first.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture) < TimeSpan.FromHours(1));
            var ownBefore = first.GetProperty("ownBytes").GetInt64();
            if (mode == "thin")
            {
                // Only what the attach wrote: far less than the image.
                Assert.True(ownBefore < imageBytes / 4, $"a new thin branch owns {ownBefore} of {imageBytes} bytes");
            }
            else
            {
                // A copy is all its own.
                Assert.True(ownBefore >= imageBytes, $"a copy branch owns {ownBefore} of {imageBytes} bytes");
            }

            // Write about 12 MiB into the branch's own files.
            await using (var connection = await ConnectAsync("pr-142"))
            {
                await ExecuteAsync(connection,
                    "CREATE TABLE dbo.Fill (Id int NOT NULL PRIMARY KEY, Pad char(4000) NOT NULL); " +
                    "INSERT dbo.Fill SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b; CHECKPOINT;");
            }

            var written = ListedBranch("pr-142").GetProperty("ownBytes").GetInt64();
            output.WriteLine($"own size before {ownBefore}, after a 12 MiB write {written}");
            Assert.True(written > ownBefore, $"{written} is not more than {ownBefore}");
            if (mode == "thin")
            {
                Assert.True(written - ownBefore >= 8L * 1024 * 1024, $"a 12 MiB write grew the own size by only {written - ownBefore} bytes");
            }

            var text = World.RunCli("branch", "list");
            output.WriteLine(text.Out);
            Assert.Equal(0, text.Exit);
            var row = text.Out.Split('\n').Single(l => l.StartsWith("pr-142", StringComparison.Ordinal));
            Assert.Contains("listimg", row, StringComparison.Ordinal);
            Assert.Contains(mode, row, StringComparison.Ordinal);
            Assert.Contains("online", row, StringComparison.Ordinal);
            Assert.Contains("MiB", row, StringComparison.Ordinal);

            // Offline.
            await CreateAsync("pr-off", "listimg");
            await fixture.SqlAsync("ALTER DATABASE [pr-off] SET OFFLINE WITH ROLLBACK IMMEDIATE");
            try
            {
                Assert.Equal("offline", ListedBranch("pr-off").GetProperty("state").GetString());
            }
            finally
            {
                await fixture.SqlAsync("ALTER DATABASE [pr-off] SET ONLINE");
            }

            Assert.Equal("online", ListedBranch("pr-off").GetProperty("state").GetString());
            await DropBranchAsync("pr-off");

            // Broken: a branch.json and no database, as a create that died leaves it.
            await InPoolAsync(
                "mkdir -p /pool/branches/dead && printf '%s' '{\"image\":\"listimg\",\"cloneMode\":\"" + mode + "\",\"createdAt\":\"2026-11-17T09:30:00+00:00\",\"labels\":{}}' > /pool/branches/dead/branch.json");
            try
            {
                var dead = ListedBranch("dead");
                Assert.Equal("broken", dead.GetProperty("state").GetString());
                Assert.Equal("listimg", dead.GetProperty("image").GetString());
                Assert.Equal(JsonValueKind.Null, dead.GetProperty("ownBytes").ValueKind);
                Assert.Equal(1, World.RunCli("status").Exit);
            }
            finally
            {
                await InPoolAsync("rm -rf /pool/branches/dead");
            }

            // A forced removal of the image: the branch is listed with the image marked removed and is still online.
            var removed = World.RunCli("image", "rm", "listimg", "--force", "--format", "json");
            Assert.True(removed.Exit == 0, removed.Out + removed.Err);
            var listed = ListedBranch("pr-142");
            output.WriteLine(listed.ToString());
            Assert.Equal("listimg", listed.GetProperty("image").GetString());
            Assert.True(listed.GetProperty("imageRemoved").GetBoolean());
            Assert.Equal("online", listed.GetProperty("state").GetString());
            var table = World.RunCli("branch", "list").Out;
            Assert.Contains("listimg (removed)", table, StringComparison.Ordinal);

            if (mode == "thin")
            {
                // Without the image, the blocks the branch never wrote are nobody else's: the whole branch is its own now.
                // The file system may take a moment to release the deleted image's references.
                var allocated = long.Parse(await InPoolAsync("du -sB1 /pool/branches/pr-142/data | cut -f1"), CultureInfo.InvariantCulture);
                long own = 0;
                for (var attempt = 0; attempt < 30 && own < allocated * 95 / 100; attempt++)
                {
                    await Task.Delay(1000);
                    own = ListedBranch("pr-142").GetProperty("ownBytes").GetInt64();
                }

                output.WriteLine($"own size after the image was removed: {own} of {allocated} allocated bytes");
                Assert.True(own >= allocated * 95 / 100, $"after the image was removed the branch owns {own} of {allocated} allocated bytes");
            }

            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("pr-142");
            await DropBranchAsync("pr-off");
            await InPoolAsync("rm -rf /pool/branches/dead");
            await RemoveImageFilesAsync("listimg");
        }
    }

    // ---- 6.9: branch conn ----

    /// <summary>
    /// The string <c>branch conn</c> prints, in text, with <c>--quiet</c> and as <c>connectionString</c> in JSON, opens the branch
    /// database and reads the image's rows. It is the one output that has the engine password; <c>branch create</c> and
    /// <c>branch list</c> do not. An unknown branch exits 1 <c>branch_not_found</c>.
    /// </summary>
    public async Task BranchConnPrintsAStringThatOpensTheBranchAsync()
    {
        await EnsureImageAsync("shop");
        try
        {
            var created = World.RunCli("branch", "create", "pr-conn", "--from", "shop", "--format", "json");
            Assert.True(created.Exit == 0, created.Out + created.Err);
            Assert.DoesNotContain(World.Password, created.Out + created.Err, StringComparison.Ordinal);

            var text = World.RunCli("branch", "conn", "pr-conn");
            var quiet = World.RunCli("branch", "conn", "pr-conn", "--quiet");
            var json = World.RunCli("branch", "conn", "pr-conn", "--format", "json");
            Assert.Equal(0, text.Exit);
            Assert.Equal(0, quiet.Exit);
            Assert.Equal(0, json.Exit);
            Assert.Equal(string.Empty, text.Err + quiet.Err + json.Err);
            Assert.Equal(text.Out, quiet.Out);
            Assert.Single(quiet.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            var fromJson = Parse(json.Out).GetProperty("connectionString").GetString()!;
            Assert.Equal(quiet.Out.Trim(), fromJson);
            Assert.Contains(World.Password, quiet.Out, StringComparison.Ordinal);

            foreach (var connectionString in new[] { quiet.Out.Trim(), fromJson })
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                Assert.Equal($"127.0.0.1,{World.Port.ToString(CultureInfo.InvariantCulture)}", builder.DataSource);
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                Assert.Equal("pr-conn", Convert.ToString(await ScalarAsync(connection, "SELECT DB_NAME()"), CultureInfo.InvariantCulture));
                Assert.Equal(100, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            }

            var list = World.RunCli("branch", "list", "--format", "json");
            var status = World.RunCli("status", "--format", "json");
            Assert.DoesNotContain(World.Password, list.Out + list.Err + status.Out + status.Err, StringComparison.Ordinal);

            var unknown = World.RunCli("branch", "conn", "nope", "--format", "json");
            Assert.Equal(1, unknown.Exit);
            Assert.Equal("branch_not_found", ErrorCode(unknown.Out));
            Assert.DoesNotContain(World.Password, unknown.Out + unknown.Err, StringComparison.Ordinal);
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("pr-conn");
        }
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public async Task VerifyPruneAsync()
    {
        await EnsureImageAsync("shop");
        var initial = new FixedClock(DateTimeOffset.UnixEpoch);
        var later = new FixedClock(initial.GetUtcNow().AddMinutes(2));
        JsonElement Prune()
        {
            var run = World.RunCliAt(later, "branch", "prune", "--format", "json");
            Assert.True(run.Exit == 0, run.Out + run.Err);
            return Parse(run.Out);
        }

        void CreateTimed(string name, string ttl)
        {
            var run = World.RunCliAt(initial, "branch", "create", name, "--from", "shop", "--ephemeral", "--ttl", ttl);
            Assert.True(run.Exit == 0, run.Out + run.Err);
        }

        try
        {
            await fixture.SqlAsync("CREATE DATABASE [scratch]");
            await CreateAsync("pr-dev", "shop");
            CreateTimed("pr-new", "1h");
            CreateTimed("pr-old", "1m");
            await using (var connection = await ConnectAsync("pr-old"))
            {
                await ExecuteAsync(connection, "BEGIN TRANSACTION; INSERT dbo.Item (Id, Name) VALUES (9001, N'uncommitted');");
                Assert.Equal("pr-old", Assert.Single(Prune().GetProperty("removed").EnumerateArray()).GetString());
                await Assert.ThrowsAnyAsync<SqlException>(() => CountAsync(connection, "SELECT 1"));
            }

            Assert.Equal("gone", await InPoolAsync("test ! -e /pool/branches/pr-old && echo gone"));
            await using (var ordinary = await ConnectAsync("pr-dev"))
            await using (var unexpired = await ConnectAsync("pr-new"))
            {
                Assert.Equal(100, await CountAsync(ordinary, "SELECT COUNT(*) FROM dbo.Item"));
                Assert.Equal(100, await CountAsync(unexpired, "SELECT COUNT(*) FROM dbo.Item"));
            }

            CreateTimed("pr-old", "1m");
            // Reset requests its clone probe id after acquiring the real SQL application lock.
            var resetter = new SqlBranch.Core.Branches.BranchResetter(World.Runtime, new SqlClientEngineConnector(),
                World.Redactor, World.EngineName, newId: () =>
                {
                    var prune = Prune();
                    Assert.Empty(prune.GetProperty("removed").EnumerateArray());
                    Assert.Equal("pr-old", Assert.Single(prune.GetProperty("skipped").EnumerateArray()).GetString());
                    return Guid.NewGuid().ToString("N")[..12];
                });
            await resetter.ResetAsync(await World.FindPoolAsync(), World.Port, "pr-old", CancellationToken.None);
            await fixture.SqlAsync("DROP DATABASE [pr-old]");
            Assert.Equal("pr-old", Assert.Single(Prune().GetProperty("removed").EnumerateArray()).GetString());
            Assert.Equal("gone", await InPoolAsync("test ! -e /pool/branches/pr-old && echo gone"));
            Assert.Equal("present", await InPoolAsync("test -f /pool/images/shop/manifest.json && echo present"));
            Assert.Equal(1, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name=N'scratch'"), CultureInfo.InvariantCulture));
            AssertStatusIsClean();
        }
        finally
        {
            foreach (var name in new[] { "pr-dev", "pr-new", "pr-old" })
            {
                await DropBranchAsync(name);
            }
            await fixture.SqlAsync("DROP DATABASE IF EXISTS [scratch]");
        }
    }

    // ---- 6.10 and 6.12: the guard and branch rm ----

    /// <summary>
    /// <c>branch rm scratch</c> on a database a user made exits 1 and the database survives; <c>branch rm ../images/shop</c> exits 2 and
    /// the image is untouched. A branch with a client holding an open connection is removed: exit 0, no database, no folder, no longer listed.
    /// A branch with a <c>branch.json</c> and no database (what a dead create leaves) loses its folder too.
    /// </summary>
    public async Task BranchRmIsGuardedAndRemovesABranchWithAnOpenConnectionAsync()
    {
        await EnsureImageAsync("shop");
        try
        {
            // The guard.
            await fixture.SqlAsync("CREATE DATABASE [scratch]");
            var notBranch = World.RunCli("branch", "rm", "scratch", "--format", "json");
            output.WriteLine(notBranch.Out + notBranch.Err);
            Assert.Equal(1, notBranch.Exit);
            Assert.Equal("branch_not_found", ErrorCode(notBranch.Out));
            Assert.Contains("not a branch", ErrorMessage(notBranch.Out), StringComparison.Ordinal);
            Assert.Equal(1, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'scratch' AND state_desc = N'ONLINE'"), CultureInfo.InvariantCulture));

            var path = World.RunCli("branch", "rm", "../images/shop", "--format", "json");
            Assert.Equal(2, path.Exit);
            Assert.Equal("invalid_name", ErrorCode(path.Out));
            Assert.Equal("1", await InPoolAsync("ls /pool/images/shop/manifest.json | wc -l"));

            var unknown = World.RunCli("branch", "rm", "nope", "--format", "json");
            Assert.Equal(1, unknown.Exit);
            Assert.Equal("branch_not_found", ErrorCode(unknown.Out));

            // A branch with an open connection.
            await CreateAsync("pr-rm", "shop");
            await using (var held = await ConnectAsync("pr-rm"))
            {
                Assert.Equal(100, await CountAsync(held, "SELECT COUNT(*) FROM dbo.Item"));
                await ExecuteAsync(held, "BEGIN TRANSACTION; INSERT dbo.Item (Id, Name) VALUES (9001, N'uncommitted');");

                var removed = World.RunCli("branch", "rm", "pr-rm", "--format", "json");
                output.WriteLine(removed.Out + removed.Err);
                Assert.True(removed.Exit == 0, removed.Out + removed.Err);
                Assert.Equal("pr-rm", Parse(removed.Out).GetProperty("branch").GetProperty("name").GetString());
                Assert.DoesNotContain(World.Password, removed.Out + removed.Err, StringComparison.Ordinal);

                // The connection was closed under the client.
                await Assert.ThrowsAnyAsync<SqlException>(() => CountAsync(held, "SELECT 1"));
            }

            Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'pr-rm'"), CultureInfo.InvariantCulture));
            Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/branches/pr-rm 2>/dev/null"));
            var list = World.RunCli("branch", "list", "--format", "json");
            Assert.DoesNotContain(Parse(list.Out).GetProperty("branches").EnumerateArray(), b => b.GetProperty("name").GetString() == "pr-rm");
            var again = World.RunCli("branch", "rm", "pr-rm", "--format", "json");
            Assert.Equal(1, again.Exit);
            Assert.Equal("branch_not_found", ErrorCode(again.Out));

            // A dead create: a record and no database. rm removes the folder.
            await InPoolAsync(
                "mkdir -p /pool/branches/dead/data && printf '%s' '{\"image\":\"shop\",\"cloneMode\":\"thin\",\"createdAt\":\"2026-11-17T09:30:00+00:00\",\"labels\":{}}' > /pool/branches/dead/branch.json");
            Assert.Equal(1, World.RunCli("status").Exit);
            var dead = World.RunCli("branch", "rm", "dead", "--format", "json");
            output.WriteLine(dead.Out + dead.Err);
            Assert.Equal(0, dead.Exit);
            Assert.False(Parse(dead.Out).GetProperty("branch").GetProperty("databaseDropped").GetBoolean());
            Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/branches/dead 2>/dev/null"));

            // The user's database is still there, and status is clean.
            Assert.Equal(1, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'scratch'"), CultureInfo.InvariantCulture));
            AssertStatusIsClean();
        }
        finally
        {
            await fixture.SqlAsync(
                "IF DB_ID(N'scratch') IS NOT NULL ALTER DATABASE [scratch] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                "IF DB_ID(N'scratch') IS NOT NULL DROP DATABASE [scratch]");
            await DropBranchAsync("pr-rm");
            await InPoolAsync("rm -rf /pool/branches/dead");
        }
    }

    /// <summary>
    /// A branch whose database is OFFLINE: <c>branch list</c> shows it offline, <c>branch conn</c> still prints the string and warns,
    /// <c>branch rm</c> removes it (no SINGLE_USER on an offline database) and a second offline branch is brought back by <c>branch reset</c>.
    /// </summary>
    public async Task OfflineBranchesCanBeRemovedAndResetAsync()
    {
        await EnsureImageAsync("offimg");
        try
        {
            await CreateAsync("pr-off-rm", "offimg");
            await fixture.SqlAsync("ALTER DATABASE [pr-off-rm] SET OFFLINE WITH ROLLBACK IMMEDIATE");
            Assert.Equal("OFFLINE", Convert.ToString(await fixture.ScalarAsync("SELECT state_desc FROM sys.databases WHERE name = N'pr-off-rm'"), CultureInfo.InvariantCulture));
            var list = World.RunCli("branch", "list", "--format", "json");
            Assert.Equal("offline", Parse(list.Out).GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "pr-off-rm").GetProperty("state").GetString());

            var conn = World.RunCli("branch", "conn", "pr-off-rm", "--format", "json");
            Assert.Equal(0, conn.Exit);
            Assert.Contains("offline", conn.Err, StringComparison.Ordinal);

            var removed = World.RunCli("branch", "rm", "pr-off-rm", "--format", "json");
            output.WriteLine(removed.Out + removed.Err);
            Assert.True(removed.Exit == 0, removed.Out + removed.Err);
            Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'pr-off-rm'"), CultureInfo.InvariantCulture));
            Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/branches/pr-off-rm 2>/dev/null"));

            await CreateAsync("pr-off-reset", "offimg");
            await fixture.SqlAsync("ALTER DATABASE [pr-off-reset] SET OFFLINE WITH ROLLBACK IMMEDIATE");
            var reset = World.RunCli("branch", "reset", "pr-off-reset", "--format", "json");
            output.WriteLine(reset.Out + reset.Err);
            Assert.True(reset.Exit == 0, reset.Out + reset.Err);
            Assert.Equal("ONLINE", Convert.ToString(await fixture.ScalarAsync("SELECT state_desc FROM sys.databases WHERE name = N'pr-off-reset'"), CultureInfo.InvariantCulture));
            await using (var connection = await ConnectAsync("pr-off-reset"))
            {
                Assert.Equal(100, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            }

            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("pr-off-rm");
            await DropBranchAsync("pr-off-reset");
            await RemoveImageFilesAsync("offimg");
        }
    }

    // ---- 6.11: branch reset ----

    /// <summary>
    /// A table dropped in a branch comes back with the image's rows after <c>branch reset</c>, the connection string printed before the
    /// reset still works, a client holding a connection is disconnected, no <c>next/</c> is left, and a branch whose image was removed
    /// with <c>--force</c> cannot be reset (exit 1) and keeps its data. On a thin pool a <c>--copy</c> branch is still a copy afterwards.
    /// </summary>
    public async Task BranchResetBringsBackADroppedTableAndKeepsTheConnectionStringAsync()
    {
        await EnsureImageAsync("resetimg");
        var mode = await CloneModeAsync();
        try
        {
            await CreateAsync("pr-reset", "resetimg");
            var before = World.RunCli("branch", "conn", "pr-reset", "--quiet").Out.Trim();
            Assert.Contains("pr-reset", before, StringComparison.Ordinal);

            await using var held = await ConnectAsync("pr-reset");
            Assert.Equal(100, await CountAsync(held, "SELECT COUNT(*) FROM dbo.Item"));
            await ExecuteAsync(held, "DROP TABLE dbo.Item; CREATE TABLE dbo.Extra (Id int NOT NULL); CHECKPOINT;");

            // The client leaves a transaction open, which the reset rolls back by closing the connection.
            await ExecuteAsync(held, "BEGIN TRANSACTION; INSERT dbo.Extra (Id) VALUES (1);");

            await using (var changed = new SqlConnection(before))
            {
                await changed.OpenAsync();
                await Assert.ThrowsAnyAsync<SqlException>(() => CountAsync(changed, "SELECT COUNT(*) FROM dbo.Item"));
                Assert.Equal(1, await CountAsync(changed, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Extra'"));
            }

            var reset = World.RunCli("branch", "reset", "pr-reset", "--format", "json");
            output.WriteLine(reset.Out + reset.Err);
            output.WriteLine($"branch reset: {reset.Elapsed.TotalSeconds:F1} s");
            Assert.True(reset.Exit == 0, reset.Out + reset.Err);
            var branch = Parse(reset.Out).GetProperty("branch");
            Assert.Equal("pr-reset", branch.GetProperty("name").GetString());
            Assert.Equal("resetimg", branch.GetProperty("image").GetString());
            Assert.Equal(mode, branch.GetProperty("cloneMode").GetString());
            Assert.DoesNotContain(World.Password, reset.Out + reset.Err, StringComparison.Ordinal);

            // The client that held a connection with an open transaction lost it (an idle connection may reconnect by itself).
            await Assert.ThrowsAnyAsync<SqlException>(() => CountAsync(held, "SELECT 1"));

            // The string from before the reset opens the new database: the table is back with the image's rows, the other table is gone.
            await using (var after = new SqlConnection(before))
            {
                await after.OpenAsync();
                Assert.Equal("pr-reset", Convert.ToString(await ScalarAsync(after, "SELECT DB_NAME()"), CultureInfo.InvariantCulture));
                Assert.Equal(100, await CountAsync(after, "SELECT COUNT(*) FROM dbo.Item"));
                Assert.Equal(0, await CountAsync(after, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Extra'"));
                await ExecuteAsync(after, "INSERT dbo.Item (Id, Name) VALUES (7777, N'after the reset'); CHECKPOINT;");
            }

            Assert.Equal(string.Empty, await InPoolAsync("ls -A /pool/branches/pr-reset | grep -x next"));
            Assert.Equal(2, (await InPoolAsync("ls /pool/branches/pr-reset/data")).Split('\n').Length);
            var record = Parse(await InPoolAsync("cat /pool/branches/pr-reset/branch.json"));
            Assert.Equal(mode, record.GetProperty("cloneMode").GetString());
            Assert.False(record.TryGetProperty("state", out _));
            Assert.Equal("online", ListedBranch("pr-reset").GetProperty("state").GetString());
            AssertStatusIsClean();

            // A second reset takes the row written after the first away again.
            Assert.Equal(0, World.RunCli("branch", "reset", "pr-reset").Exit);
            await using (var again = new SqlConnection(before))
            {
                await again.OpenAsync();
                Assert.Equal(0, await CountAsync(again, "SELECT COUNT(*) FROM dbo.Item WHERE Id = 7777"));
            }

            if (mode == "thin")
            {
                await CreateAsync("bench-reset", "resetimg", "--copy");
                var copyReset = World.RunCli("branch", "reset", "bench-reset", "--format", "json");
                Assert.True(copyReset.Exit == 0, copyReset.Out + copyReset.Err);
                Assert.Equal("copy", Parse(copyReset.Out).GetProperty("branch").GetProperty("cloneMode").GetString());
                Assert.Equal("copy", ListedBranch("bench-reset").GetProperty("cloneMode").GetString());
            }

            // The guard.
            var user = World.RunCli("branch", "reset", "nope", "--format", "json");
            Assert.Equal(1, user.Exit);
            Assert.Equal("branch_not_found", ErrorCode(user.Out));
            var badName = World.RunCli("branch", "reset", "../images/resetimg", "--format", "json");
            Assert.Equal(2, badName.Exit);

            // The image is removed with --force: the branch cannot be reset and stays online with its data.
            await using (var edit = await ConnectAsync("pr-reset"))
            {
                await ExecuteAsync(edit, "INSERT dbo.Item (Id, Name) VALUES (8888, N'kept'); CHECKPOINT;");
            }

            Assert.Equal(0, World.RunCli("image", "rm", "resetimg", "--force").Exit);
            var gone = World.RunCli("branch", "reset", "pr-reset", "--format", "json");
            output.WriteLine(gone.Out + gone.Err);
            Assert.Equal(1, gone.Exit);
            Assert.Equal("image_not_found", ErrorCode(gone.Out));
            Assert.Contains("no longer exists", ErrorMessage(gone.Out), StringComparison.Ordinal);
            Assert.Equal("online", ListedBranch("pr-reset").GetProperty("state").GetString());
            await using (var kept = await ConnectAsync("pr-reset"))
            {
                Assert.Equal(1, await CountAsync(kept, "SELECT COUNT(*) FROM dbo.Item WHERE Id = 8888"));
            }

            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("pr-reset");
            await DropBranchAsync("bench-reset");
            await RemoveImageFilesAsync("resetimg");
        }
    }

    // ---- 6.13: defaultImage ----

    /// <summary>
    /// With <c>SQLBRANCH_DEFAULT_IMAGE</c> set, <c>branch create dev1</c> without <c>--from</c> creates the branch from that image; without
    /// it the command exits 2 naming <c>--from</c> and creates nothing.
    /// </summary>
    public async Task DefaultImageIsUsedWithoutFromAsync()
    {
        await EnsureImageAsync("shop");
        try
        {
            var refused = World.RunCli("branch", "create", "dev1", "--format", "json");
            output.WriteLine(refused.Out + refused.Err);
            Assert.Equal(2, refused.Exit);
            Assert.Contains("--from", ErrorMessage(refused.Out), StringComparison.Ordinal);
            Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/dev1 2>/dev/null"));

            World.Environment["SQLBRANCH_DEFAULT_IMAGE"] = "shop";
            var created = World.RunCli("branch", "create", "dev1", "--format", "json");
            output.WriteLine(created.Out + created.Err);
            Assert.True(created.Exit == 0, created.Out + created.Err);
            Assert.Equal("shop", Parse(created.Out).GetProperty("branch").GetProperty("image").GetString());
            await using var connection = await ConnectAsync("dev1");
            Assert.Equal(100, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            AssertStatusIsClean();
        }
        finally
        {
            World.Environment.Remove("SQLBRANCH_DEFAULT_IMAGE");
            await DropBranchAsync("dev1");
        }
    }

    // ---- 6.14: DBCC CHECKDB on written branches ----

    /// <summary>
    /// Runs <c>DBCC CHECKDB</c> on <paramref name="database"/> and asserts it printed nothing: no error and no message. CHECKDB normally
    /// reads a hidden snapshot of the database, a sparse file next to the data files. A pool folder that the container host serves from the
    /// client's file system (the copy-mode test pool) cannot make one (operating system error 31), so the check then runs with
    /// <c>TABLOCK</c>, which reads the live database under locks: the same checks, no snapshot.
    /// </summary>
    private async Task AssertCheckDbCleanAsync(string database)
    {
        foreach (var options in new[] { "NO_INFOMSGS, ALL_ERRORMSGS", "NO_INFOMSGS, ALL_ERRORMSGS, TABLOCK" })
        {
            await using var connection = await ConnectAsync(database);
            var messages = new List<string>();
            connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SqlError>().Select(x => $"{x.Number}: {x.Message}"));
            await using var command = new SqlCommand($"DBCC CHECKDB (N'{database}') WITH {options}", connection) { CommandTimeout = 1800 };
            try
            {
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    messages.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
                }
            }
            catch (SqlException ex) when (!options.Contains("TABLOCK", StringComparison.Ordinal) && ex.Message.Contains("snapshot cannot be created", StringComparison.Ordinal))
            {
                output.WriteLine($"DBCC CHECKDB {database}: no internal snapshot on this pool ({ex.Errors[^1].Message}); running with TABLOCK");
                continue;
            }

            output.WriteLine($"DBCC CHECKDB {database} WITH {options}: {messages.Count} messages");
            Assert.True(messages.Count == 0, "DBCC CHECKDB reported: " + string.Join("; ", messages));
            return;
        }
    }

    /// <summary>
    /// <c>DBCC CHECKDB</c> reports no errors on a branch that was written to, and on a branch of an image whose 256 MB source log was
    /// rebuilt (task 5.14) after one million rows were inserted (the spec's "Writes after a rebuilt log"). The rows are small so the
    /// insert is quick under emulation: a clustered int key and an int, 1,000,000 rows.
    /// </summary>
    public async Task CheckDbReportsNoErrorsOnWrittenBranchesAsync()
    {
        var mode = await CloneModeAsync();
        await EnsureImageAsync("shop");
        const string BigLogImage = "rebuilt";
        try
        {
            // An ordinary branch, with inserts, updates and deletes.
            await CreateAsync("chk-shop", "shop");
            await using (var connection = await ConnectAsync("chk-shop"))
            {
                await ExecuteAsync(connection,
                    "INSERT dbo.Item (Id, Name) SELECT TOP (20000) 1000 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), REPLICATE(N'w', 40) FROM sys.all_objects a CROSS JOIN sys.all_objects b; " +
                    "UPDATE dbo.Item SET Name = N'changed' WHERE Id % 3 = 0; DELETE dbo.Item WHERE Id % 7 = 0; CHECKPOINT;");
            }

            await AssertCheckDbCleanAsync("chk-shop");

            // A source with a 256 MB log, rebuilt at seal time, then a million rows in its branch.
            await fixture.SqlAsync(
                "CREATE DATABASE [BigLogChk]",
                "ALTER DATABASE [BigLogChk] MODIFY FILE (NAME = BigLogChk_log, SIZE = 256MB)",
                "EXEC [BigLogChk].sys.sp_executesql N'CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, V int NOT NULL); INSERT dbo.T VALUES (1, 1), (2, 2), (3, 3);'",
                "BACKUP DATABASE [BigLogChk] TO DISK = N'/tmp/biglogchk.bak' WITH INIT");
            await fixture.SqlAsync("DROP DATABASE [BigLogChk]");
            var bak = await fixture.TakeFromEngineAsync("biglogchk.bak");
            var build = World.RunCli("image", "build", BigLogImage, "--from-bak", bak, "--format", "json");
            output.WriteLine(build.Out + build.Err);
            Assert.True(build.Exit == 0, build.Out + build.Err);
            Assert.True(Parse(await InPoolAsync($"cat /pool/images/{BigLogImage}/manifest.json")).GetProperty("logRebuilt").GetBoolean());

            var created = World.RunCli("branch", "create", "chk-log", "--from", BigLogImage, "--format", "json");
            output.WriteLine(created.Out + created.Err);
            Assert.Equal(0, created.Exit);
            Assert.Equal(mode, Parse(created.Out).GetProperty("branch").GetProperty("cloneMode").GetString());

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await using (var connection = await ConnectAsync("chk-log"))
            {
                await ExecuteAsync(connection,
                    "INSERT dbo.T (Id, V) SELECT TOP (1000000) 1000 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 7 FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c; CHECKPOINT;");
                Assert.Equal(1_000_003, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.T"));
            }

            output.WriteLine($"one million rows inserted in {clock.Elapsed.TotalSeconds:F0} s ({mode} pool)");
            clock.Restart();
            await AssertCheckDbCleanAsync("chk-log");
            output.WriteLine($"DBCC CHECKDB of the million-row branch took {clock.Elapsed.TotalSeconds:F0} s: no errors");
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("chk-shop");
            await DropBranchAsync("chk-log");
            await RemoveImageFilesAsync(BigLogImage);
        }
    }

    // ---- 6.15: --copy on a thin pool ----

    private async Task<string> RecordedModeAsync(string branch) =>
        Parse(await InPoolAsync($"cat /pool/branches/{branch}/branch.json")).GetProperty("cloneMode").GetString()!;

    /// <summary>
    /// On a thin pool a branch made with <c>--copy</c> from an image of 1 GiB and more reports <c>copy</c>, grows the pool's used
    /// space by at least 900 MiB, and is listed as <c>copy</c>. Thin mode only.
    /// </summary>
    public async Task CopyBranchOnAThinPoolIsAFullCopyAsync()
    {
        SkipTestException.UnlessThin(await CloneModeAsync());
        const long OneGiB = 1L << 30;
        var bak = await fixture.MakeLargeBackupAsync("BigCopy", 140_000);
        var build = World.RunCli("image", "build", "bigcopy", "--from-bak", bak, "--format", "json");
        output.WriteLine($"image build: {build.Elapsed.TotalSeconds:F0} s");
        Assert.True(build.Exit == 0, build.Out + build.Err);
        var imageBytes = Parse(build.Out).GetProperty("image").GetProperty("sizeBytes").GetInt64();
        Assert.True(imageBytes >= OneGiB, $"the image is {imageBytes} bytes, under 1 GiB");

        try
        {
            var before = await PoolUsedBytesAsync();
            var create = World.RunCli("branch", "create", "bench1", "--from", "bigcopy", "--copy", "--format", "json");
            output.WriteLine($"bench1: {create.Elapsed.TotalSeconds:F1} s");
            Assert.True(create.Exit == 0, create.Out + create.Err);
            Assert.Equal("copy", Parse(create.Out).GetProperty("branch").GetProperty("cloneMode").GetString());

            var growth = (await PoolUsedBytesAsync()) - before;
            output.WriteLine($"pool used space grew by {growth} bytes ({growth / 1048576.0:F1} MiB) for a --copy branch of a {imageBytes / 1048576.0:F0} MiB image on a thin pool");
            Assert.True(growth >= 900L * 1024 * 1024, $"the pool grew by {growth} bytes");

            var listed = ListedBranch("bench1");
            Assert.Equal("copy", listed.GetProperty("cloneMode").GetString());
            Assert.Equal("online", listed.GetProperty("state").GetString());
            Assert.Equal("copy", await RecordedModeAsync("bench1"));
            await using var connection = await ConnectAsync("bench1");
            Assert.Equal(140_000, await CountAsync(connection, "SELECT COUNT(*) FROM dbo.Item"));
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("bench1");
            await RemoveImageFilesAsync("bigcopy");
        }
    }

    /// <summary>
    /// A branch made with <c>--copy</c> on a thin pool is still a copy after a reset: the reset reports <c>copy</c>, the listing and
    /// <c>branch.json</c> say <c>copy</c>, and none of its data files shares an extent with the image. Thin mode only.
    /// </summary>
    public async Task CopyBranchOnAThinPoolStaysACopyAfterAResetAsync()
    {
        SkipTestException.UnlessThin(await CloneModeAsync());
        await EnsureImageAsync("shop");
        try
        {
            await CreateAsync("bench2", "shop", "--copy");
            await using (var connection = await ConnectAsync("bench2"))
            {
                await ExecuteAsync(connection, "DROP TABLE dbo.Item; CHECKPOINT;");
            }

            var reset = World.RunCli("branch", "reset", "bench2", "--format", "json");
            output.WriteLine(reset.Out + reset.Err);
            Assert.True(reset.Exit == 0, reset.Out + reset.Err);
            Assert.Equal("copy", Parse(reset.Out).GetProperty("branch").GetProperty("cloneMode").GetString());
            Assert.Equal("copy", ListedBranch("bench2").GetProperty("cloneMode").GetString());
            Assert.Equal("copy", await RecordedModeAsync("bench2"));

            // Full copies: no extent of a branch file is shared with another file.
            var shared = await InPoolAsync("for f in /pool/branches/bench2/data/*; do filefrag -v \"$f\"; done | grep -c shared || true");
            output.WriteLine("shared extents: " + shared);
            Assert.Equal("0", shared);

            await using var after = await ConnectAsync("bench2");
            Assert.Equal(100, await CountAsync(after, "SELECT COUNT(*) FROM dbo.Item"));
            Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/bench2/next 2>/dev/null"));
            AssertStatusIsClean();
        }
        finally
        {
            await DropBranchAsync("bench2");
        }
    }
}

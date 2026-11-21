using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlBranch.Core.Engine;
using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Tasks 6.1 to 6.3 on the real engine: <c>branch create</c>, the undo of a failed create and the lock of a branch name.
/// One engine for the class, with the image <c>shop</c> built once from a backup of a database with 100 rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class BranchCreateIntegrationTests(ImageEngineFixture fixture, ITestOutputHelper output) : IClassFixture<ImageEngineFixture>
{
    private const string ShopBackup = "Shop";

    private EngineWorld World => fixture.World!;

    private async Task<string> InPoolAsync(string script) =>
        (await World.Runtime.ExecAsync(World.EngineName, ["sh", "-c", script], CancellationToken.None)).StdOut.Trim();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string ErrorCode(string json) => Parse(json).GetProperty("error").GetProperty("code").GetString()!;

    private static string ErrorMessage(string json) => Parse(json).GetProperty("error").GetProperty("message").GetString()!;

    /// <summary>Builds image <paramref name="image"/> from a backup of database Shop (100 rows) unless it is already there.</summary>
    private async Task EnsureImageAsync(string image = "shop")
    {
        if ((await InPoolAsync($"ls /pool/images/{image}/manifest.json 2>/dev/null")).Length > 0)
        {
            return;
        }

        var bak = Path.Combine(fixture.ClientFolder, ShopBackup + ".bak");
        if (!File.Exists(bak))
        {
            bak = await fixture.MakeBackupAsync(ShopBackup);
        }

        var run = World.RunCli("image", "build", image, "--from-bak", bak, "--format", "json");
        Assert.True(run.Exit == 0, run.Out + run.Err);
    }

    /// <summary>Cleanup of a branch the test made: <c>branch rm</c> first, then the raw cleanup on this world's own engine, so nothing is left behind.</summary>
    private async Task DropBranchAsync(string name) => await Kit.DropBranchAsync(name);

    private async Task<string> CloneModeOfPoolAsync() =>
        Parse(World.RunCli("status", "--format", "json").Out).GetProperty("pool").GetProperty("cloneMode").GetString()!;

    private void AssertStatusIsClean()
    {
        var status = World.RunCli("status", "--format", "json");
        output.WriteLine(status.Out);
        Assert.Equal(0, status.Exit);
        Assert.Empty(Parse(status.Out).GetProperty("mismatches").EnumerateArray());
    }

    /// <summary>A connection built from what <c>branch create</c> returned and the password in the pool's credentials file.</summary>
    private async Task<SqlConnection> ConnectAsync(string server, string database, string user)
    {
        var pool = await World.FindPoolAsync();
        var password = await EngineCredentials.ReadAsync(World.Runtime, pool.Engine!.Image, pool.Location, CancellationToken.None);
        Assert.NotNull(password);
        var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = user,
            Password = password,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [IntegrationFact]
    public void The_test_engine_started()
    {
        Assert.True(fixture.Init.Exit == 0, fixture.Init.Out + fixture.Init.Err);
    }

    [IntegrationFact]
    public async Task A_branch_returns_its_connection_facts_and_the_image_rows_are_readable_through_them_without_the_password_in_the_output()
    {
        await EnsureImageAsync();
        var expectedMode = await CloneModeOfPoolAsync();
        try
        {
            var run = World.RunCli("branch", "create", "pr-142", "--from", "shop", "--format", "json");
            output.WriteLine(run.Out + run.Err);
            output.WriteLine($"branch create: {run.Elapsed.TotalSeconds:F1} s");
            Assert.True(run.Exit == 0, run.Out + run.Err);
            Assert.DoesNotContain(World.Password, run.Out + run.Err, StringComparison.Ordinal);

            var branch = Parse(run.Out).GetProperty("branch");
            Assert.Equal("pr-142", branch.GetProperty("name").GetString());
            Assert.Equal("shop", branch.GetProperty("image").GetString());
            Assert.Equal(expectedMode, branch.GetProperty("cloneMode").GetString());
            Assert.Equal($"127.0.0.1,{World.Port.ToString(CultureInfo.InvariantCulture)}", branch.GetProperty("server").GetString());
            Assert.Equal("pr-142", branch.GetProperty("database").GetString());
            Assert.Equal("sa", branch.GetProperty("user").GetString());

            // The rows of the image, through a connection made from the returned facts and the stored password.
            await using var connection = await ConnectAsync(
                branch.GetProperty("server").GetString()!, branch.GetProperty("database").GetString()!, branch.GetProperty("user").GetString()!);
            Assert.Equal("pr-142", await ScalarAsync(connection, "SELECT DB_NAME()"));
            Assert.Equal(100, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Item"), CultureInfo.InvariantCulture));

            // The branch is writable.
            await using (var insert = new SqlCommand("INSERT dbo.Item (Id, Name) VALUES (1000, N'mine')", connection))
            {
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            // The files and the record in the pool.
            var record = Parse(await InPoolAsync("cat /pool/branches/pr-142/branch.json"));
            Assert.Equal("shop", record.GetProperty("image").GetString());
            Assert.Equal(expectedMode, record.GetProperty("cloneMode").GetString());
            Assert.True(record.TryGetProperty("createdAt", out _));
            Assert.Equal(JsonValueKind.Object, record.GetProperty("labels").ValueKind);
            var stat = await InPoolAsync("stat -c '%a %u %n' /pool/branches/pr-142/data/*");
            output.WriteLine(stat);
            Assert.Equal(2, stat.Split('\n').Length);
            Assert.All(stat.Split('\n'), line => Assert.StartsWith("640 10001 ", line, StringComparison.Ordinal));

            // The image is untouched and read-only, and status finds nothing wrong.
            Assert.All((await InPoolAsync("stat -c '%a' /pool/images/shop/data/*")).Split('\n'), mode => Assert.Equal("444", mode));
            AssertStatusIsClean();

            // The same name again is refused and the first branch keeps what was written to it.
            var again = World.RunCli("branch", "create", "pr-142", "--from", "shop", "--format", "json");
            Assert.Equal(1, again.Exit);
            Assert.Equal("branch_exists", ErrorCode(again.Out));
            Assert.Contains("already exists", ErrorMessage(again.Out), StringComparison.Ordinal);
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Item WHERE Id = 1000"), CultureInfo.InvariantCulture));
        }
        finally
        {
            await DropBranchAsync("pr-142");
        }
    }

    [IntegrationFact]
    public async Task A_name_used_by_a_user_database_is_refused_and_the_database_is_left_alone_and_other_refusals_make_nothing()
    {
        await EnsureImageAsync();
        await fixture.SqlAsync("CREATE DATABASE [scratch]");
        try
        {
            var run = World.RunCli("branch", "create", "scratch", "--from", "shop", "--format", "json");
            output.WriteLine(run.Out);
            Assert.Equal(1, run.Exit);
            Assert.Equal("branch_exists", ErrorCode(run.Out));
            Assert.Contains("database named 'scratch' already exists", ErrorMessage(run.Out), StringComparison.Ordinal);
            Assert.Equal(1, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'scratch'"), CultureInfo.InvariantCulture));
            Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/scratch 2>/dev/null"));

            var missing = World.RunCli("branch", "create", "pr-1", "--from", "missing", "--format", "json");
            Assert.Equal(1, missing.Exit);
            Assert.Equal("image_not_found", ErrorCode(missing.Out));
            Assert.Contains("missing", ErrorMessage(missing.Out), StringComparison.Ordinal);

            var reserved = World.RunCli("branch", "create", "master", "--from", "shop", "--format", "json");
            Assert.Equal(2, reserved.Exit);
            Assert.Contains("reserved", ErrorMessage(reserved.Out), StringComparison.Ordinal);
            var prefix = World.RunCli("branch", "create", "sqlbranch_build_x", "--from", "shop", "--format", "json");
            Assert.Equal(2, prefix.Exit);
            Assert.Contains("sqlbranch_", ErrorMessage(prefix.Out), StringComparison.Ordinal);

            Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/pr-1 /pool/branches/master 2>/dev/null"));
            AssertStatusIsClean();
        }
        finally
        {
            await fixture.SqlAsync("DROP DATABASE IF EXISTS [scratch]");
        }
    }

    [IntegrationFact]
    public async Task A_create_whose_attach_fails_leaves_no_database_no_folder_and_status_exits_0()
    {
        await EnsureImageAsync();
        await EnsureImageAsync("broken");
        try
        {
            // Zero the file header page of the broken image's data file: the files clone, and the engine cannot attach them.
            var corrupt = await InPoolAsync(
                "f=$(ls /pool/images/broken/data/*.mdf | head -n 1); chmod u+w \"$f\" && dd if=/dev/zero of=\"$f\" bs=8192 count=1 conv=notrunc 2>/dev/null && chmod 444 \"$f\" && echo done");
            Assert.Equal("done", corrupt);

            var run = World.RunCli("branch", "create", "pr-bad", "--from", "broken", "--format", "json");
            output.WriteLine(run.Out + run.Err);
            Assert.Equal(1, run.Exit);
            // The files cloned and the engine refused them: the failure is the attach's, not a step in the shell.
            Assert.DoesNotContain("could not do this step", ErrorMessage(run.Out), StringComparison.Ordinal);
            output.WriteLine("attach error: " + ErrorMessage(run.Out));
            Assert.Equal(0, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'pr-bad'"), CultureInfo.InvariantCulture));
            Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/pr-bad 2>/dev/null"));
            AssertStatusIsClean();

            // The good image still makes a branch after the failed one, so nothing was left in the way.
            var good = World.RunCli("branch", "create", "pr-good", "--from", "shop", "--format", "json");
            Assert.True(good.Exit == 0, good.Out + good.Err);
        }
        finally
        {
            await DropBranchAsync("pr-bad");
            await DropBranchAsync("pr-good");
            await InPoolAsync("rm -rf /pool/images/broken");
        }
    }

    [IntegrationFact]
    public async Task Two_simultaneous_creates_of_one_name_give_exit_codes_0_and_1_and_leave_no_mismatch()
    {
        await EnsureImageAsync();
        try
        {
            using var first = World.StartCli("branch", "create", "pr-9", "--from", "shop", "--format", "json");
            using var second = World.StartCli("branch", "create", "pr-9", "--from", "shop", "--format", "json");
            var a = await first.WaitAsync(TimeSpan.FromMinutes(5));
            var b = await second.WaitAsync(TimeSpan.FromMinutes(5));
            output.WriteLine(a.Out + a.Err);
            output.WriteLine(b.Out + b.Err);

            Assert.Equal([0, 1], new[] { a.Exit, b.Exit }.Order());
            var loser = a.Exit == 1 ? a : b;
            Assert.Equal("branch_exists", ErrorCode(loser.Out));
            Assert.Equal(1, Convert.ToInt32(await fixture.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = N'pr-9'"), CultureInfo.InvariantCulture));
            AssertStatusIsClean();

            // The survivor is a whole branch.
            var winner = Parse(a.Exit == 0 ? a.Out : b.Out).GetProperty("branch");
            await using var connection = await ConnectAsync(winner.GetProperty("server").GetString()!, "pr-9", "sa");
            Assert.Equal(100, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Item"), CultureInfo.InvariantCulture));
        }
        finally
        {
            await DropBranchAsync("pr-9");
        }
    }

    [IntegrationFact]
    public async Task The_branch_lock_is_honored_and_creates_of_different_names_from_one_image_run_together_while_the_image_lock_keeps_image_rm_out()
    {
        await EnsureImageAsync();
        try
        {
            // A session that holds the name's lock keeps a create of that name out.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using (var holder = await new SqlClientEngineConnector().ConnectAsync(World.Endpoint, World.Password, cts.Token))
            {
                Assert.True(await EngineLocks.TryTakeAsync(holder, EngineLocks.Branch("pr-locked"), cts.Token));
                var blocked = World.RunCli("branch", "create", "pr-locked", "--from", "shop", "--format", "json");
                Assert.Equal(1, blocked.Exit);
                Assert.Contains("Another command is creating or changing the branch 'pr-locked'", ErrorMessage(blocked.Out), StringComparison.Ordinal);
                Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/pr-locked 2>/dev/null"));
            }

            // Two creates from one image with different names both succeed.
            using var first = World.StartCli("branch", "create", "pr-a", "--from", "shop", "--format", "json");
            using var second = World.StartCli("branch", "create", "pr-b", "--from", "shop", "--format", "json");
            var a = await first.WaitAsync(TimeSpan.FromMinutes(5));
            var b = await second.WaitAsync(TimeSpan.FromMinutes(5));
            output.WriteLine(a.Out + a.Err + b.Out + b.Err);
            Assert.Equal(0, a.Exit);
            Assert.Equal(0, b.Exit);
            AssertStatusIsClean();

            // A create that is running holds its image in shared mode: 'image rm' cannot take it.
            await using (var running = await new SqlClientEngineConnector().ConnectAsync(World.Endpoint, World.Password, cts.Token))
            {
                Assert.True(await EngineLocks.TryTakeSharedAsync(running, EngineLocks.Image("shop"), cts.Token));
                var remove = World.RunCli("image", "rm", "shop", "--force", "--format", "json");
                Assert.Equal(1, remove.Exit);
                Assert.Contains("Another command is working on the image", ErrorMessage(remove.Out), StringComparison.Ordinal);
                Assert.Equal("1", await InPoolAsync("ls /pool/images/shop/manifest.json | wc -l"));
            }

            // And an exclusive holder, such as a build or a removal that is running, keeps a create out.
            await using (var exclusiveHolder = await new SqlClientEngineConnector().ConnectAsync(World.Endpoint, World.Password, cts.Token))
            {
                Assert.True(await EngineLocks.TryTakeAsync(exclusiveHolder, EngineLocks.Image("shop"), cts.Token));
                var refused = World.RunCli("branch", "create", "pr-c", "--from", "shop", "--format", "json");
                Assert.Equal(1, refused.Exit);
                Assert.Contains("building or removing the image 'shop'", ErrorMessage(refused.Out), StringComparison.Ordinal);
                Assert.Equal(string.Empty, await InPoolAsync("ls /pool/branches/pr-c 2>/dev/null"));
            }
        }
        finally
        {
            await DropBranchAsync("pr-a");
            await DropBranchAsync("pr-b");
        }
    }
}

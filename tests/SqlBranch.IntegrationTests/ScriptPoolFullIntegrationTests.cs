using SqlBranch.Core.Pools;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ScriptPoolFullIntegrationTests
{
    [IntegrationFact]
    public async Task A_seed_script_that_exhausts_a_bounded_copy_pool_is_cleaned_up()
    {
        await using var world = new EngineWorld(plain: false);
        var paths = world.Paths;
        var files = Directory.CreateTempSubdirectory("sqlbranch-it-scripts-");
        try
        {
            // The standard test-world disposer unmounts and deletes this world's loop file.
            // ext4 without reflinks exercises the real copy backend on a bounded filesystem.
            var format = await EngineWorld.DockerAsync("run", "--rm", "-v", $"{paths.Root}:{paths.Root}",
                ManagedPool.HelperImage, "sh", "-ec",
                $"truncate -s 1G '{paths.PoolFile}' && mkfs.ext4 -q -m 0 '{paths.PoolFile}'");
            Assert.True(format.ExitCode == 0, format.StdErr);
            await new ManagedPool(world.Runtime, paths).MountAsync(CancellationToken.None);
            world.Environment["SQLBRANCH_POOL_BACKEND"] = "plain";
            world.Environment["SQLBRANCH_POOL"] = paths.MountPoint + "/pool";
            var init = world.RunCli("init");
            Assert.True(init.Exit == 0, init.Err);
            var status = world.RunCli("status", "--format", "json");
            var pool = BranchKit.Parse(status.Out).GetProperty("pool");
            Assert.Equal("copy", pool.GetProperty("cloneMode").GetString());
            Assert.InRange(pool.GetProperty("totalBytes").GetInt64(), 512L << 20, 1L << 30);
            var reserve = pool.GetProperty("freeBytes").GetInt64() - (96L << 20);
            Assert.True(reserve > 0);
            var fill = await EngineWorld.DockerAsync("exec", "--user", "0", world.EngineName, "fallocate", "-l",
                reserve.ToString(System.Globalization.CultureInfo.InvariantCulture), "/pool/test-reservation");
            Assert.True(fill.ExitCode == 0, fill.StdErr);

            await File.WriteAllTextAsync(Path.Combine(files.FullName, "001_schema.sql"), "CREATE TABLE dbo.Seed (v char(7000));");
            await File.WriteAllTextAsync(Path.Combine(files.FullName, "002_seed.sql"),
                "INSERT dbo.Seed SELECT TOP (30000) REPLICATE('x', 7000) FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
            var run = world.RunCli("image", "build", "overflow", "--from-scripts", files.FullName, "--format", "json");
            Assert.Equal(1, run.Exit);
            Assert.Equal("script_failed", BranchKit.ErrorCode(run.Out));
            Assert.Contains("002_seed.sql", BranchKit.ErrorMessage(run.Out), StringComparison.Ordinal);
            Assert.Equal(0, world.RunCli("status").Exit);
            var listing = world.RunCli("image", "list", "--format", "json");
            Assert.Empty(BranchKit.Parse(listing.Out).GetProperty("images").EnumerateArray());
        }
        finally
        {
            files.Delete(recursive: true);
        }
    }
}

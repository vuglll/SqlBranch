using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class TeardownCommandTests
{
    private readonly CliWorld _world = new();

    private static string ErrorCode(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    private static string ErrorMessage(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("message").GetString()!;

    private void PlainPool(bool running = true)
    {
        _world.WithEngine(running);
        _world.Directories.Add(FakeInitHost.PoolDir);
        _world.Connector.Databases["scratch"] = ["/pool/engine/data/scratch.mdf"];
    }

    private void ManagedPool(bool running = true, bool mounted = true)
    {
        _world.WithEngine(running, "loop-xfs", ManagedPaths.Default.MountPoint);
        _world.Host.PoolFileExists = true;
        _world.Host.Mounted = mounted;
        _world.Connector.Databases["scratch"] = ["/pool/engine/data/scratch.mdf"];
    }

    private void VolumePool()
    {
        _world.WithEngine(true, "plain", ContainerLabels.PoolVolume);
        _world.Runtime.Volumes.Add(ContainerLabels.PoolVolume);
        _world.Connector.Databases["scratch"] = ["/pool/engine/data/scratch.mdf"];
    }

    private (int Exit, string Out, string Err) Teardown(params string[] extra) => _world.Run(["teardown", "--force", "--format", "json", .. extra]);

    [Fact]
    public void A_volume_without_the_managed_label_is_left_alone_with_no_folder_script_and_no_removal()
    {
        VolumePool();
        _world.Runtime.UnlabeledVolumes.Add(ContainerLabels.PoolVolume);
        _world.Host.MarkerExists = true;

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.Empty(_world.Host.FolderRuns);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("volume rm", StringComparison.Ordinal));
        Assert.Contains(ContainerLabels.PoolVolume, _world.Runtime.Volumes);
        Assert.True(_world.Host.EngineDirExists);
        Assert.True(_world.Host.MarkerExists);
        Assert.Contains("was not created by SqlBranch", run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_volume_pool_is_torn_down_only_behind_the_marker_guard_and_by_the_name_given()
    {
        _world.PoolVolume = "sqlbranch-it-pool-x1";
        _world.WithEngine(true, "plain", "sqlbranch-it-pool-x1");
        _world.Runtime.Volumes.Add("sqlbranch-it-pool-x1");
        _world.Runtime.Volumes.Add(ContainerLabels.PoolVolume);

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.StartsWith("if [ -f /pool/.sqlbranch-pool ]; then", _world.Host.FolderRuns.Single(), StringComparison.Ordinal);
        Assert.Contains("volume rm sqlbranch-it-pool-x1", _world.Runtime.Calls);
        Assert.DoesNotContain("volume rm sqlbranch-pool", _world.Runtime.Calls);
        Assert.Contains(ContainerLabels.PoolVolume, _world.Runtime.Volumes);
    }

    // Guards

    [Fact]
    public void Without_force_it_exits_2_names_force_and_removes_nothing()
    {
        PlainPool();
        var callsBefore = _world.Runtime.Calls.Count;

        var run = _world.Run("teardown", "--format", "json");

        Assert.Equal(2, run.Exit);
        Assert.Equal("usage_error", ErrorCode(run.Out));
        Assert.Contains("--force", ErrorMessage(run.Out), StringComparison.Ordinal);
        Assert.Equal(callsBefore, _world.Runtime.Calls.Count);
        Assert.Contains(ContainerLabels.EngineName, _world.Runtime.Containers.Keys);
        Assert.Single(_world.Connector.Databases);
    }

    [Fact]
    public void With_nothing_found_it_exits_3_not_initialized_and_creates_nothing()
    {
        var run = Teardown();

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", ErrorCode(run.Out));
        Assert.DoesNotContain(_world.Runtime.Runs, r => r.Detach || r.Privileged);
        Assert.DoesNotContain(_world.Host.Scripts, s => s.Contains("rm -rf", StringComparison.Ordinal));
    }

    [Fact]
    public void A_container_with_the_engines_name_that_sqlbranch_did_not_create_is_never_touched()
    {
        _world.Runtime.Containers[ContainerLabels.EngineName] = new ContainerInfo(
            ContainerLabels.EngineName, "other:1", true, new Dictionary<string, string>(), []);

        var run = Teardown();

        Assert.Equal(3, run.Exit);
        Assert.Equal("foreign_container", ErrorCode(run.Out));
        Assert.True(_world.Runtime.Containers[ContainerLabels.EngineName].Running);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("rm ", StringComparison.Ordinal) || c.StartsWith("stop", StringComparison.Ordinal));
    }

    // Success paths

    [Fact]
    public void A_plain_pool_loses_only_sqlbranchs_folders_and_the_directory_stays()
    {
        PlainPool();
        _world.Host.ImportFiles.Add("shop.bak");

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        var removed = JsonDocument.Parse(run.Out).RootElement.GetProperty("removed");
        Assert.Equal(["scratch"], removed.GetProperty("databases").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["shop.bak"], removed.GetProperty("importBackups").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(_world.Runtime.Containers);
        Assert.False(_world.Host.MarkerExists);
        Assert.False(_world.Host.EngineDirExists);
        Assert.Empty(_world.Connector.Databases);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("volume rm", StringComparison.Ordinal));
        Assert.DoesNotContain(_world.Runtime.Runs, r => r.Privileged || r.HostPid);

        var script = _world.Host.FolderRuns.Single();
        Assert.Contains("rm -rf /pool/engine /pool/images /pool/branches /pool/import /pool/build /pool/.probe", script, StringComparison.Ordinal);
        Assert.EndsWith("&& rm -f /pool/.sqlbranch-pool && echo SQLBRANCH_TEARDOWN=done; else echo SQLBRANCH_TEARDOWN=nothing; fi", script, StringComparison.Ordinal);
        // The marker is deleted after every folder, and the directory itself never.
        Assert.True(script.IndexOf("rm -rf", StringComparison.Ordinal) < script.IndexOf("rm -f /pool/.sqlbranch-pool", StringComparison.Ordinal));
        Assert.DoesNotContain("/pool ", script.Replace("/pool/", "", StringComparison.Ordinal) + " ", StringComparison.Ordinal);
        Assert.Contains("if [ -f /pool/.sqlbranch-pool ]; then", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_output_lists_the_user_databases_and_the_import_backups()
    {
        PlainPool();
        _world.Host.ImportFiles.Add("shop.bak");

        var run = _world.Run("teardown", "--force");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Removed database: scratch", run.Out, StringComparison.Ordinal);
        Assert.Contains("Removed import backup: shop.bak", run.Out, StringComparison.Ordinal);
        Assert.Contains("every other file in it are kept", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_engine_is_started_to_list_and_drop_the_databases_then_removed()
    {
        PlainPool(running: false);

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.Contains("scratch", run.Out, StringComparison.Ordinal);
        Assert.Contains($"start {ContainerLabels.EngineName}", _world.Runtime.Calls);
        Assert.True(_world.Runtime.Calls.IndexOf($"start {ContainerLabels.EngineName}") < _world.Runtime.Calls.IndexOf($"rm {ContainerLabels.EngineName}"));
        Assert.Empty(_world.Runtime.Containers);
    }

    [Fact]
    public void A_managed_pool_loses_its_mount_and_its_file()
    {
        ManagedPool();

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.False(_world.Host.PoolFileExists);
        Assert.False(_world.Host.Mounted);
        Assert.Empty(_world.Runtime.Containers);
        Assert.Contains("scratch", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_managed_pool_that_was_unmounted_is_mounted_again_before_the_engine_starts()
    {
        ManagedPool(running: false, mounted: false);

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.False(_world.Host.PoolFileExists);
        Assert.False(_world.Host.Mounted);
        Assert.Contains("scratch", run.Out, StringComparison.Ordinal);
        var mount = _world.Runtime.Runs.FindIndex(r => FakeContainerRuntime.ScriptOf(r).Contains("mount -o loop", StringComparison.Ordinal));
        var start = _world.Runtime.Calls.IndexOf($"start {ContainerLabels.EngineName}");
        Assert.True(mount >= 0 && start >= 0);
    }

    [Fact]
    public void A_managed_pool_file_with_no_engine_is_found_and_removed()
    {
        _world.Host.PoolFileExists = true;
        _world.Host.Mounted = true;

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.False(_world.Host.PoolFileExists);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public void A_managed_pool_that_cannot_be_mounted_is_still_removed_with_a_warning()
    {
        ManagedPool(running: false, mounted: false);
        _world.Host.FailMount = true;

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.False(_world.Host.PoolFileExists);
        Assert.Contains("could not be mounted", run.Err, StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Containers);
    }

    [Fact]
    public void The_copy_mode_volume_is_removed()
    {
        VolumePool();

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.Empty(_world.Runtime.Volumes);
        Assert.Empty(_world.Runtime.Containers);
    }

    [Fact]
    public void An_engine_that_will_not_start_falls_back_to_the_database_files_that_are_not_system_files()
    {
        PlainPool(running: false);
        _world.Connector.NeverReady = true;
        _world.Host.DataFiles.AddRange(["master.mdf", "mastlog.ldf", "tempdb.mdf", "scratch.mdf", "scratch_log.ldf", "Entropy.bin"]);

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        var removed = JsonDocument.Parse(run.Out).RootElement.GetProperty("removed");
        Assert.Empty(removed.GetProperty("databases").EnumerateArray());
        Assert.Equal(["scratch.mdf", "scratch_log.ldf"], removed.GetProperty("databaseFiles").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(_world.Runtime.Containers);
        Assert.False(_world.Host.MarkerExists);
    }

    [Fact]
    public void A_database_that_cannot_be_dropped_is_reported_and_the_teardown_goes_on()
    {
        PlainPool();
        _world.Connector.FailDrop.Add("scratch");

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.Contains("Could not drop the database scratch", run.Err, StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Containers);
    }

    [Fact]
    public void A_database_name_with_a_bracket_is_quoted()
    {
        Assert.Equal("ALTER DATABASE [a]]b] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [a]]b];", TeardownRunner.DropSql("a]b"));
    }

    [Fact]
    public void The_pool_found_with_only_its_marker_is_cleaned_without_an_engine()
    {
        _world.Directories.Add(FakeInitHost.PoolDir);
        _world.Host.MarkerExists = true;

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.False(_world.Host.MarkerExists);
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public void No_container_is_left_whatever_the_exit_code()
    {
        PlainPool();
        _world.Host.FailFolders = true;

        var run = Teardown();

        Assert.Equal(1, run.Exit);
        Assert.Empty(_world.Runtime.Containers);
        Assert.All(_world.Runtime.Runs.Where(r => r.Name.StartsWith("sqlbranch-work-", StringComparison.Ordinal)),
            r => Assert.True(r.RemoveWhenDone && !r.Privileged));
    }

    // A failure after each step, then the same command again

    [Fact]
    public void A_failed_removal_of_the_engine_container_exits_1_naming_it_and_a_second_run_finishes()
    {
        PlainPool();
        _world.Runtime.FailRemove.Add(ContainerLabels.EngineName);

        var first = Teardown();

        Assert.Equal(1, first.Exit);
        Assert.Equal("operation_failed", ErrorCode(first.Out));
        Assert.Contains($"the engine container {ContainerLabels.EngineName}", ErrorMessage(first.Out), StringComparison.Ordinal);

        _world.Runtime.FailRemove.Clear();
        var second = Teardown();

        Assert.Equal(0, second.Exit);
        Assert.Empty(_world.Runtime.Containers);
    }

    [Fact]
    public void A_failed_folder_removal_on_a_plain_pool_exits_1_naming_the_pool_and_a_second_run_finishes()
    {
        PlainPool();
        _world.Host.FailFolders = true;

        var first = Teardown();

        Assert.Equal(1, first.Exit);
        Assert.Contains("the SqlBranch folders and the marker", ErrorMessage(first.Out), StringComparison.Ordinal);
        Assert.Contains(FakeInitHost.PoolDir, ErrorMessage(first.Out), StringComparison.Ordinal);
        Assert.True(_world.Host.MarkerExists);

        _world.Host.FailFolders = false;
        var second = Teardown();

        Assert.Equal(0, second.Exit);
        Assert.False(_world.Host.MarkerExists);
        Assert.False(_world.Host.EngineDirExists);
    }

    [Fact]
    public void A_failed_unmount_exits_1_naming_the_mount_point_and_the_file_and_a_second_run_finishes()
    {
        ManagedPool();
        _world.Host.FailUnmount = true;

        var first = Teardown();

        Assert.Equal(1, first.Exit);
        Assert.Contains("/var/lib/sqlbranch/mnt", ErrorMessage(first.Out), StringComparison.Ordinal);
        Assert.Contains("/var/lib/sqlbranch/pool.xfs", ErrorMessage(first.Out), StringComparison.Ordinal);
        Assert.True(_world.Host.PoolFileExists);
        Assert.Empty(_world.Runtime.Containers);

        _world.Host.FailUnmount = false;
        var second = Teardown();

        Assert.Equal(0, second.Exit);
        Assert.False(_world.Host.PoolFileExists);
        Assert.False(_world.Host.Mounted);
    }

    [Fact]
    public void A_failed_delete_of_the_pool_file_exits_1_naming_it_and_a_second_run_finishes()
    {
        ManagedPool();
        _world.Host.FailDelete = true;

        var first = Teardown();

        Assert.Equal(1, first.Exit);
        Assert.Contains("/var/lib/sqlbranch/pool.xfs", ErrorMessage(first.Out), StringComparison.Ordinal);

        _world.Host.FailDelete = false;
        var second = Teardown();

        Assert.Equal(0, second.Exit);
        Assert.False(_world.Host.PoolFileExists);
    }

    [Fact]
    public void A_failed_folder_removal_on_a_managed_pool_is_covered_by_deleting_the_file()
    {
        ManagedPool();
        _world.Host.FailFolders = true;

        var run = Teardown();

        Assert.Equal(0, run.Exit);
        Assert.False(_world.Host.PoolFileExists);
    }

    [Fact]
    public void A_failed_removal_of_the_volume_exits_1_naming_it_and_a_second_run_finishes()
    {
        VolumePool();
        _world.Runtime.FailRemoveVolume.Add(ContainerLabels.PoolVolume);

        var first = Teardown();

        Assert.Equal(1, first.Exit);
        Assert.Contains(ContainerLabels.PoolVolume, ErrorMessage(first.Out), StringComparison.Ordinal);
        Assert.Contains(ContainerLabels.PoolVolume, _world.Runtime.Volumes);

        _world.Runtime.FailRemoveVolume.Clear();
        var second = Teardown();

        Assert.Equal(0, second.Exit);
        Assert.Empty(_world.Runtime.Volumes);
    }

    // What the folder script can delete

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_folder_script_names_only_constants_under_pool(bool guarded)
    {
        var script = TeardownRunner.FoldersScript(guarded);

        Assert.Equal(["engine", "images", "branches", "import", "build", ".probe"], TeardownRunner.PoolItems);
        foreach (var token in script.Split(' ').Where(t => t.StartsWith("/pool", StringComparison.Ordinal)))
        {
            Assert.Matches(@"^/pool(/(engine|images|branches|import|build|\.probe|\.sqlbranch-pool)(/\*|/\.\[!\.\]\*)?;?|/import/\*|/import/\.\[!\.\]\*)$", token.TrimEnd(';'));
        }

        Assert.DoesNotContain("find", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$", script.Replace("${f##*/}", string.Empty, StringComparison.Ordinal).Replace("\"$f\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}

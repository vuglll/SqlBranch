using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Pools;

public sealed class CloneProbeTests
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";
    private readonly FakeContainerRuntime _runtime = new();
    private readonly PoolLocation _pool = new(PoolKind.Directory, "/data/sb");

    [Theory]
    [InlineData("SQLBRANCH_PROBE=thin\n", CloneMode.Thin)]
    [InlineData("SQLBRANCH_PROBE=copy\n", CloneMode.Copy)]
    public async Task The_marker_in_the_output_gives_the_mode(string output, CloneMode expected)
    {
        _runtime.RunHandler = _ => FakeContainerRuntime.Ok(output);

        Assert.Equal(expected, await CloneProbe.ProbeAsync(_runtime, Image, _pool, CancellationToken.None));
    }

    [Fact]
    public async Task A_probe_that_prints_no_marker_is_an_error_not_a_mode()
    {
        _runtime.RunHandler = _ => FakeContainerRuntime.Fail(1, "read-only file system");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => CloneProbe.ProbeAsync(_runtime, Image, _pool, CancellationToken.None));

        Assert.Contains("read-only file system", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_probe_container_is_labeled_unprivileged_mounts_only_the_pool_and_is_removed()
    {
        _runtime.RunHandler = _ => FakeContainerRuntime.Ok("SQLBRANCH_PROBE=copy");

        await CloneProbe.ProbeAsync(_runtime, Image, _pool, CancellationToken.None);

        var run = Assert.Single(_runtime.Runs);
        Assert.False(run.Privileged);
        Assert.False(run.HostPid);
        Assert.True(run.RemoveWhenDone);
        Assert.Equal(new MountSpec(MountKind.Bind, "/data/sb", "/pool"), Assert.Single(run.Mounts));
        Assert.Contains("cp --reflink=always", FakeContainerRuntime.ScriptOf(run), StringComparison.Ordinal);
        Assert.Contains($"rm {run.Name}", _runtime.Calls);
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task The_work_container_is_removed_even_when_the_run_throws()
    {
        _runtime.RunHandler = _ => throw new SqlBranchException("daemon went away");

        await Assert.ThrowsAsync<SqlBranchException>(() => CloneProbe.ProbeAsync(_runtime, Image, _pool, CancellationToken.None));

        Assert.Contains(_runtime.Calls, c => c.StartsWith("rm sqlbranch-work-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_volume_pool_is_mounted_as_a_volume()
    {
        _runtime.RunHandler = _ => FakeContainerRuntime.Ok("SQLBRANCH_PROBE=copy");

        await CloneProbe.ProbeAsync(_runtime, Image, new PoolLocation(PoolKind.Volume, "sqlbranch-pool"), CancellationToken.None);

        Assert.Equal(MountKind.Volume, Assert.Single(Assert.Single(_runtime.Runs).Mounts).Kind);
    }

    [Fact]
    public async Task The_engine_probe_runs_in_the_engine_container()
    {
        _runtime.Containers[ContainerLabels.EngineName] = new ContainerInfo(
            ContainerLabels.EngineName, Image, true, new Dictionary<string, string> { [ContainerLabels.Managed] = "true" }, []);
        _runtime.ExecHandler = (_, _) => FakeContainerRuntime.Ok("SQLBRANCH_PROBE=thin");

        Assert.Equal(CloneMode.Thin, await CloneProbe.ProbeInEngineAsync(_runtime, CancellationToken.None));
        Assert.Empty(_runtime.Runs);
    }
}

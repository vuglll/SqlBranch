using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Pools;

public sealed class DirectoryPoolTests
{
    private const string Image = "img";
    private readonly FakeContainerRuntime _runtime = new();
    private readonly PoolLocation _pool = new(PoolKind.Directory, "/data/work");
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private void Contents(string state) =>
        _runtime.RunHandler = spec => FakeContainerRuntime.ScriptOf(spec).Contains("SQLBRANCH_POOL=", StringComparison.Ordinal)
            ? FakeContainerRuntime.Ok($"SQLBRANCH_POOL={state}")
            : FakeContainerRuntime.Ok();

    [Theory]
    [InlineData("empty", DirectoryState.Empty)]
    [InlineData("marked", DirectoryState.Marked)]
    [InlineData("foreign", DirectoryState.Foreign)]
    public async Task Inspect_reads_the_state(string output, DirectoryState expected)
    {
        Contents(output);

        Assert.Equal(expected, await DirectoryPool.InspectAsync(_runtime, Image, _pool, CancellationToken.None));
    }

    [Fact]
    public async Task A_foreign_directory_is_refused_with_a_config_error_naming_it_and_nothing_is_written()
    {
        Contents("foreign");

        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            DirectoryPool.InitializeAsync(_runtime, Image, _pool, Now, CancellationToken.None));

        Assert.Contains("/data/work", ex.Message, StringComparison.Ordinal);
        Assert.All(_runtime.Runs, run => Assert.True(Assert.Single(run.Mounts).ReadOnly, "the inspection must mount read-only"));
        Assert.DoesNotContain(_runtime.Runs, run => FakeContainerRuntime.ScriptOf(run).Contains("chown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("marked")]
    public async Task An_empty_or_marked_directory_gets_the_marker_if_missing_and_the_engine_as_owner(string state)
    {
        Contents(state);

        await DirectoryPool.InitializeAsync(_runtime, Image, _pool, Now, CancellationToken.None);

        var write = _runtime.Runs.Last();
        var script = FakeContainerRuntime.ScriptOf(write);
        Assert.False(Assert.Single(write.Mounts).ReadOnly);
        Assert.Contains("[ -f /pool/.sqlbranch-pool ] ||", script, StringComparison.Ordinal);
        Assert.Contains("\"format\":1", script, StringComparison.Ordinal);
        Assert.Contains("chown -R 10001:0 /pool", script, StringComparison.Ordinal);
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task A_failed_write_is_reported()
    {
        _runtime.RunHandler = spec => FakeContainerRuntime.ScriptOf(spec).Contains("SQLBRANCH_POOL=", StringComparison.Ordinal)
            ? FakeContainerRuntime.Ok("SQLBRANCH_POOL=empty")
            : FakeContainerRuntime.Fail(1, "chown: Operation not permitted");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            DirectoryPool.InitializeAsync(_runtime, Image, _pool, Now, CancellationToken.None));

        Assert.Contains("Operation not permitted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_carries_a_format_version_and_a_time()
    {
        Assert.Equal("{\"format\":1,\"created\":\"2026-11-16T09:00:00Z\"}", PoolMarker.Json(Now));
    }
}

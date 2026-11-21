using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Engine;

public sealed class PoolFinderTests
{
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeEngineConnector _connector = new();
    private readonly FakeInitHost _host;
    private readonly Settings _settings = new() { Pool = FakeInitHost.PoolDir };

    public PoolFinderTests()
    {
        _host = new FakeInitHost(_runtime, _connector);
    }

    private PoolFinder Finder(bool directoryExists = true) =>
        new(_runtime, ManagedPaths.Default, _ => directoryExists);

    private Task<FoundPool?> Find(bool directoryExists = true) =>
        Finder(directoryExists).FindAsync(_settings, "/home/u/.sqlbranch/pool", CancellationToken.None);

    [Fact]
    public async Task The_engines_labels_come_first_even_when_other_pools_exist()
    {
        _host.AddExistingEngine("img", "plain", "/data/sb");
        _host.PoolFileExists = true;
        _runtime.Volumes.Add(ContainerLabels.PoolVolume);

        var found = await Find();

        Assert.NotNull(found);
        Assert.Equal(PoolSource.EngineLabels, found.Source);
        Assert.Equal(new PoolLocation(PoolKind.Directory, "/data/sb"), found.Location);
        Assert.NotNull(found.Engine);
        // The first lookup answered, so no helper or work container ran.
        Assert.Empty(_runtime.Runs);
    }

    [Fact]
    public async Task The_managed_pool_file_comes_second()
    {
        _host.PoolFileExists = true;
        _runtime.Volumes.Add(ContainerLabels.PoolVolume);

        var found = await Find();

        Assert.NotNull(found);
        Assert.Equal(PoolSource.ManagedFile, found.Source);
        Assert.Equal("loop-xfs", found.Backend);
        Assert.Equal(ManagedPaths.Default.Location, found.Location);
        Assert.Null(found.Engine);
    }

    [Fact]
    public async Task The_volume_comes_third()
    {
        _runtime.Volumes.Add(ContainerLabels.PoolVolume);
        _host.MarkerExists = true;

        var found = await Find();

        Assert.NotNull(found);
        Assert.Equal(PoolSource.Volume, found.Source);
        Assert.Equal(new PoolLocation(PoolKind.Volume, ContainerLabels.PoolVolume), found.Location);
    }

    [Fact]
    public async Task A_volume_without_the_managed_label_is_not_a_pool()
    {
        _runtime.Volumes.Add(ContainerLabels.PoolVolume);
        _runtime.UnlabeledVolumes.Add(ContainerLabels.PoolVolume);

        var found = await Find(directoryExists: false);

        Assert.Null(found);
        // Nothing was mounted to look inside it.
        Assert.DoesNotContain(_runtime.Runs, r => r.Mounts.Any(m => m.Source == ContainerLabels.PoolVolume));
    }

    [Fact]
    public async Task The_volume_name_is_the_one_given_and_the_product_default_is_not_looked_at()
    {
        _runtime.Volumes.Add(ContainerLabels.PoolVolume);
        _runtime.Volumes.Add("sqlbranch-it-pool-abc");

        var other = await new PoolFinder(_runtime, ManagedPaths.Default, _ => false, poolVolume: "sqlbranch-it-pool-abc")
            .FindAsync(_settings, "/x", CancellationToken.None);
        _runtime.Volumes.Remove("sqlbranch-it-pool-abc");
        var gone = await new PoolFinder(_runtime, ManagedPaths.Default, _ => false, poolVolume: "sqlbranch-it-pool-abc")
            .FindAsync(_settings, "/x", CancellationToken.None);

        Assert.Equal(new PoolLocation(PoolKind.Volume, "sqlbranch-it-pool-abc"), other?.Location);
        Assert.Null(gone);
    }

    [Fact]
    public async Task The_configured_directory_counts_only_with_the_marker()
    {
        _host.MarkerExists = true;
        var marked = await Find();
        _host.MarkerExists = false;
        var unmarked = await Find();

        Assert.NotNull(marked);
        Assert.Equal(PoolSource.Directory, marked.Source);
        Assert.Equal("plain", marked.Backend);
        Assert.Null(unmarked);
    }

    [Fact]
    public async Task A_missing_directory_is_never_mounted()
    {
        _host.MarkerExists = true;

        var found = await Find(directoryExists: false);

        Assert.Null(found);
        // A bind mount of a missing host folder would create it, so no work container may mount it.
        Assert.DoesNotContain(_runtime.Runs, r => r.Mounts.Any(m => m.Source == FakeInitHost.PoolDir));
    }

    [Fact]
    public async Task Nothing_found_is_not_initialized_naming_init()
    {
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            Finder().RequireAsync(_settings, "/x", CancellationToken.None));

        Assert.Equal(ErrorCodes.NotInitialized, ex.Code);
        Assert.Equal(ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
        Assert.Contains("sqlbranch init", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_container_is_never_taken_for_the_engine()
    {
        _runtime.Containers[ContainerLabels.EngineName] =
            new ContainerInfo(ContainerLabels.EngineName, "postgres:16", true, new Dictionary<string, string>(), []);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Find());

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
    }

    [Fact]
    public async Task Docker_missing_is_docker_unavailable_and_not_a_missing_pool()
    {
        _runtime.InfoFailure = new SqlBranchException(ErrorCodes.DockerUnavailable, "no docker");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Find());

        Assert.Equal(ErrorCodes.DockerUnavailable, ex.Code);
    }
}

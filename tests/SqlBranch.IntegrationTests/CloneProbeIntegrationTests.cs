using SqlBranch.Core.Pools;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class CloneProbeIntegrationTests
{
    [IntegrationFact]
    public async Task The_probe_reports_a_mode_for_a_real_directory_and_leaves_nothing_behind()
    {
        var runtime = DockerHost.Runtime();
        await using var pool = new HostPoolDirectory(runtime);

        var mode = await CloneProbe.ProbeAsync(runtime, DockerHost.Image, pool.Location, CancellationToken.None);

        // The mode depends on the file system under the temp folder: thin on XFS with reflink or Btrfs,
        // copy elsewhere. What must hold on every machine is that the probe answers and cleans up.
        Assert.True(mode is CloneMode.Thin or CloneMode.Copy);
        Assert.Empty(Directory.GetFileSystemEntries(pool.Path));
        Assert.DoesNotContain(await DockerHost.ManagedContainerNamesAsync(runtime), n => n.StartsWith("sqlbranch-work-", StringComparison.Ordinal));
    }
}

using SqlBranch.Core.Configuration;
using SqlBranch.Core.Pools;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ManagedPoolIntegrationTests
{
    private static readonly ByteSize TwoGigabytes = new(2_000_000_000);

    [IntegrationThinFact]
    public async Task A_managed_pool_reports_thin_and_leaves_no_helper_container()
    {
        SkipTestException.UnlessManagedRequested();
        var runtime = DockerHost.Runtime();
        var id = DockerHost.NewId();
        var paths = DockerHost.TestPaths(id);
        var pool = new ManagedPool(runtime, paths);
        var lines = new List<string>();

        try
        {
            await pool.EnsureHostSpaceAsync(TwoGigabytes, CancellationToken.None);
            await pool.CreateAsync(TwoGigabytes, lines.Add, CancellationToken.None);

            Assert.True(await pool.FileExistsAsync(CancellationToken.None));
            Assert.True(await pool.IsMountedAsync(CancellationToken.None));
            Assert.Equal(TwoGigabytes.Bytes, await pool.FileSizeAsync(CancellationToken.None));
            Assert.Equal(
                [$"Pool file: {paths.PoolFile} (2 GB)", $"Mount point: {paths.MountPoint}"],
                lines);

            var mode = await CloneProbe.ProbeAsync(runtime, DockerHost.Image, paths.Location, CancellationToken.None);

            Assert.Equal(CloneMode.Thin, mode);
            Assert.DoesNotContain(await DockerHost.ManagedContainerNamesAsync(runtime), n => n.StartsWith("sqlbranch-", StringComparison.Ordinal));
        }
        finally
        {
            await pool.UnmountAsync(CancellationToken.None);
            await pool.DeleteFileAsync(CancellationToken.None);
        }

        try
        {
            // These checks run helpers that bind the folder, which makes Docker create it again, so it goes last.
            Assert.False(await pool.FileExistsAsync(CancellationToken.None));
            Assert.False(await pool.IsMountedAsync(CancellationToken.None));
            Assert.DoesNotContain(await DockerHost.ManagedContainerNamesAsync(runtime), n => n.StartsWith("sqlbranch-", StringComparison.Ordinal));
        }
        finally
        {
            await DockerHost.RemoveTestFolderAsync(runtime, paths, id);
        }
    }
}

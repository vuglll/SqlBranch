using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;

namespace SqlBranch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DirectoryPoolIntegrationTests
{
    /// <summary>Lists every entry of the directory with its owner, read-only, so the test can compare before and after.</summary>
    private static async Task<string> SnapshotAsync(IContainerRuntime runtime, HostPoolDirectory pool)
    {
        var result = await PoolWork.RunAsync(runtime, DockerHost.Image, pool.Location,
            "find /pool -exec stat -c '%n %u:%g %s' {} + | sort", readOnly: true, CancellationToken.None);
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut;
    }

    private static async Task SeedAsync(IContainerRuntime runtime, HostPoolDirectory pool, string script)
    {
        var result = await PoolWork.RunAsync(runtime, DockerHost.Image, pool.Location, script, readOnly: false, CancellationToken.None);
        Assert.True(result.Succeeded, result.StdErr);
    }

    [IntegrationFact]
    public async Task A_folder_with_notes_txt_is_refused_and_unchanged()
    {
        var runtime = DockerHost.Runtime();
        await using var pool = new HostPoolDirectory(runtime);
        await SeedAsync(runtime, pool, "echo hello > /pool/notes.txt && chown 1234:1234 /pool/notes.txt");
        var before = await SnapshotAsync(runtime, pool);

        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            DirectoryPool.InitializeAsync(runtime, DockerHost.Image, pool.Location, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains(pool.Path, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(runtime, pool));
        Assert.Contains("notes.txt 1234:1234", before, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task A_folder_holding_only_images_photo_png_is_refused_and_unchanged()
    {
        var runtime = DockerHost.Runtime();
        await using var pool = new HostPoolDirectory(runtime);
        await SeedAsync(runtime, pool, "mkdir /pool/images && echo png > /pool/images/photo.png && chown -R 1234:1234 /pool/images");
        var before = await SnapshotAsync(runtime, pool);

        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            DirectoryPool.InitializeAsync(runtime, DockerHost.Image, pool.Location, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains(pool.Path, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(runtime, pool));
        Assert.Contains("images/photo.png 1234:1234", before, StringComparison.Ordinal);
    }

    [IntegrationFact]
    public async Task An_empty_folder_gets_the_marker_and_a_second_init_is_accepted()
    {
        var runtime = DockerHost.Runtime();
        await using var pool = new HostPoolDirectory(runtime);

        await DirectoryPool.InitializeAsync(runtime, DockerHost.Image, pool.Location, DateTimeOffset.UtcNow, CancellationToken.None);
        await DirectoryPool.InitializeAsync(runtime, DockerHost.Image, pool.Location, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(DirectoryState.Marked, await DirectoryPool.InspectAsync(runtime, DockerHost.Image, pool.Location, CancellationToken.None));
        Assert.Contains("10001:0", await SnapshotAsync(runtime, pool), StringComparison.Ordinal);
    }
}

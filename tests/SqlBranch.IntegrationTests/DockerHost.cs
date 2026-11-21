using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.IntegrationTests;

/// <summary>Shared pieces for tests that use the real Docker. Everything they create is labeled and removed by the test.</summary>
internal static class DockerHost
{
    /// <summary>The image the work and probe containers use. Override with SQLBRANCH_IT_IMAGE.</summary>
    public static string Image =>
        Environment.GetEnvironmentVariable("SQLBRANCH_IT_IMAGE") is { Length: > 0 } image
            ? image
            : "mcr.microsoft.com/mssql/server:2022-latest";

    public static DockerRuntime Runtime() => new(new SystemProcessRunner(), new SecretRedactor());

    /// <summary>A managed-pool folder of its own under /var/lib/sqlbranch, so a real pool is never touched.</summary>
    public static ManagedPaths TestPaths(string id) => new($"{ManagedPaths.DefaultRoot}/it-{id}");

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Removes the empty <c>it-&lt;id&gt;</c> folder in the container host that every managed pool test leaves,
    /// and the empty mount point inside it. Retries because the mount can take a moment to let go.
    /// </summary>
    public static async Task RemoveTestFolderAsync(IContainerRuntime runtime, ManagedPaths paths, string id)
    {
        var name = $"sqlbranch-it-rmdir-{id}";
        var spec = new ContainerSpec
        {
            Name = name,
            Image = ManagedPool.HelperImage,
            RemoveWhenDone = true,
            Mounts = [new MountSpec(MountKind.Bind, ManagedPaths.DefaultRoot, ManagedPaths.DefaultRoot)],
            Command = ["sh", "-c",
                $"for i in 1 2 3 4 5 6 7 8 9 10; do rmdir '{paths.MountPoint}' 2>/dev/null; rmdir '{paths.Root}' && break; sleep 1; done; true"],
        };
        try
        {
            await runtime.RunAsync(spec, CancellationToken.None);
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
        }
    }

    /// <summary>The labeled containers that exist now.</summary>
    public static async Task<IReadOnlyList<string>> ManagedContainerNamesAsync(IContainerRuntime runtime) =>
        [.. (await runtime.ListManagedAsync(CancellationToken.None)).Select(c => c.Name)];
}

/// <summary>A host directory for a test pool, deleted through a container afterwards because files in it may be owned by another user.</summary>
internal sealed class HostPoolDirectory : IAsyncDisposable
{
    private readonly IContainerRuntime _runtime;

    public HostPoolDirectory(IContainerRuntime runtime)
    {
        _runtime = runtime;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlbranch-it-" + DockerHost.NewId());
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public PoolLocation Location => new(PoolKind.Directory, Path);

    public async ValueTask DisposeAsync()
    {
        try
        {
            // The pool chowns /pool to the engine user. Give the empty directory back to the test
            // user, or a non-root user cannot remove it from a sticky /tmp.
            await PoolWork.RunAsync(_runtime, DockerHost.Image, Location,
                $"find /pool -mindepth 1 -delete && chown {HostUser.Id}:{HostUser.Group} /pool", readOnly: false, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

/// <summary>The numeric user and group of the test process, for giving files back after a container changed their owner.</summary>
internal static class HostUser
{
    private static readonly Lazy<string> IdValue = new(() => Run("-u"));
    private static readonly Lazy<string> GroupValue = new(() => Run("-g"));

    public static string Id => IdValue.Value;

    public static string Group => GroupValue.Value;

    private static string Run(string flag)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", flag)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not run id.");
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return uint.TryParse(value, out _) ? value : throw new InvalidOperationException($"id {flag} printed '{value}'.");
    }
}

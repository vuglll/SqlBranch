using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Pools;

/// <summary>
/// Runs a fixed shell script in a short-lived container that mounts only the
/// pool. The container is labeled, not privileged and removed when the step
/// ends, whether it succeeded or failed.
/// </summary>
public static class PoolWork
{
    public static async Task<ContainerResult> RunAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        string script,
        bool readOnly,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(pool);

        var name = "sqlbranch-work-" + Guid.NewGuid().ToString("N")[..8];
        var spec = new ContainerSpec
        {
            Name = name,
            Image = image,
            Platform = ImagePlatform.For(image),

            // Root without privileges: the SQL Server image's own user cannot change ownership, and its
            // entrypoint would start the permission checks of a database server for a one-line script.
            User = "0",
            Entrypoint = string.Empty,
            Env = environment ?? new Dictionary<string, string>(),
            Stdin = stdin,
            RemoveWhenDone = true,
            Mounts = [pool.ToMount(readOnly)],
            Command = ["sh", "-c", script],
        };

        try
        {
            return await runtime.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

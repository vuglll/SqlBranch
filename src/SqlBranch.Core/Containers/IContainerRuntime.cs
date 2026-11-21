namespace SqlBranch.Core.Containers;

/// <summary>
/// The only way SqlBranch reaches containers. The one real implementation runs
/// the <c>docker</c> command; tests use a fake. It acts only on containers and
/// volumes that carry <c>io.sqlbranch.managed=true</c>.
/// </summary>
public interface IContainerRuntime
{
    /// <summary>Facts about the host. Throws <c>docker_unavailable</c> when Docker cannot be reached.</summary>
    Task<RuntimeInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>Creates a container and waits for it to end, or returns at once when it is detached.</summary>
    Task<ContainerResult> RunAsync(ContainerSpec spec, CancellationToken cancellationToken);

    /// <summary>Runs a command in a running SqlBranch container.</summary>
    Task<ContainerResult> ExecAsync(string container, IReadOnlyList<string> command, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a command in a running SqlBranch container with its standard output to read, or its standard input to write, as a
    /// <see cref="Stream"/> (design D3). Standard error is collected; nothing else is buffered. Awaiting the returned process's
    /// <see cref="IStreamingProcess.WaitAsync"/> gives the exit code, and disposing it, or ending <paramref name="cancellationToken"/>,
    /// kills the <c>docker exec</c> client and waits for it to exit. <see cref="ExecAsync"/> is unchanged.
    /// </summary>
    Task<IStreamingProcess> ExecStreamAsync(string container, IReadOnlyList<string> command, StreamDirection direction, CancellationToken cancellationToken);

    /// <summary>
    /// Copies one file from the client into a running SqlBranch container (<c>docker cp</c>), then makes
    /// <paramref name="owner"/> (<c>uid:gid</c>) its owner with mode 640, because <c>docker cp</c> keeps the
    /// client's owner and mode and the engine's user could not read the file otherwise.
    /// </summary>
    Task CopyToAsync(string container, string hostPath, string containerPath, string owner, CancellationToken cancellationToken);

    /// <summary>Returns the container, or null when it does not exist. Works for any container, labeled or not.</summary>
    Task<ContainerInfo?> InspectAsync(string name, CancellationToken cancellationToken);

    /// <summary>Starts a stopped SqlBranch container.</summary>
    Task StartAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stops a SqlBranch container.</summary>
    Task StopAsync(string name, CancellationToken cancellationToken);

    /// <summary>Removes a SqlBranch container. Returns false when it does not exist.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>The last lines of a SqlBranch container's log.</summary>
    Task<string> LogsAsync(string name, int tail, CancellationToken cancellationToken);

    /// <summary>Every container with the managed label, running or not.</summary>
    Task<IReadOnlyList<ContainerInfo>> ListManagedAsync(CancellationToken cancellationToken);

    /// <summary>True when the image is already on the container host. Never pulls.</summary>
    Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken);

    /// <summary>True when the volume exists, labeled or not.</summary>
    Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>True when the volume exists and carries <c>io.sqlbranch.managed=true</c>. A volume without the label is not SqlBranch's.</summary>
    Task<bool> VolumeIsManagedAsync(string name, CancellationToken cancellationToken);

    /// <summary>Creates a labeled volume.</summary>
    Task CreateVolumeAsync(string name, CancellationToken cancellationToken);

    /// <summary>Removes a volume that carries the managed label. Returns false when it does not exist.</summary>
    Task<bool> RemoveVolumeAsync(string name, CancellationToken cancellationToken);
}

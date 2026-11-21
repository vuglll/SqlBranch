using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Pools;

/// <summary>A step of managed-pool work, for messages and fallback reasons.</summary>
public enum ManagedPoolStep
{
    SpaceCheck,
    Format,
    Mount,
    Unmount,
    Delete,
    Inspect,
}

/// <summary>A privileged helper step failed. <c>init</c> turns this into a fallback or exit 3.</summary>
public sealed class ManagedPoolException : Exception
{
    public ManagedPoolException()
    {
    }

    public ManagedPoolException(string message)
        : base(message)
    {
    }

    public ManagedPoolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ManagedPoolException(ManagedPoolStep step, string message)
        : base(message)
    {
        Step = step;
    }

    public ManagedPoolStep Step { get; }

    /// <summary>The exit code of the helper's script, or null when the failure did not come from one.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// The <c>loop-xfs</c> pool: an XFS file under /var/lib/sqlbranch/ on the
/// container host, formatted and mounted by privileged helper containers. Each
/// helper is labeled, mounts only that folder, runs one fixed command, and is
/// removed when its step ends.
/// </summary>
public sealed class ManagedPool
{
    /// <summary>The image the helpers run: it has <c>nsenter</c>, and xfsprogs installs from its repositories.</summary>
    public const string HelperImage = "ubuntu:24.04";

    /// <summary>The format script's exit code when the pool file already exists: nothing was created.</summary>
    public const int ExitFileExists = 66;

    /// <summary>The format script's exit code when it failed before it created the file (installing xfsprogs).</summary>
    public const int ExitBeforeCreate = 70;

    /// <summary>The exit code <c>docker run</c> gives when the daemon could not start the container at all.</summary>
    private const int ExitDockerFailed = 125;

    private readonly IContainerRuntime _runtime;
    private readonly ManagedPaths _paths;
    private readonly string _helperImage;

    public ManagedPool(IContainerRuntime runtime, ManagedPaths paths, string helperImage = HelperImage)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _helperImage = helperImage;
    }

    public ManagedPaths Paths => _paths;

    /// <summary>The lines <c>init</c> prints before it creates anything on the container host.</summary>
    public IReadOnlyList<string> DescribePlan(ByteSize size) =>
    [
        $"Pool file: {_paths.PoolFile} ({size.ToGigabytes()})",
        $"Mount point: {_paths.MountPoint}",
    ];

    /// <summary>Free bytes on the file system under the managed folder, read by a helper with <c>df</c>.</summary>
    public async Task<long> FreeBytesAsync(CancellationToken cancellationToken)
    {
        var result = await RunHelperAsync("df", ManagedPoolStep.SpaceCheck, privileged: false, hostPid: false,
            $"df -B1 --output=avail '{_paths.Root}' | tail -n 1", cancellationToken).ConfigureAwait(false);
        if (!long.TryParse(result.LastLine, NumberStyles.None, CultureInfo.InvariantCulture, out var free))
        {
            throw new ManagedPoolException(ManagedPoolStep.SpaceCheck, $"Could not read the free space of the container host from '{result.LastLine}'.");
        }

        return free;
    }

    /// <summary>Exit 3 <c>host_disk_full</c> unless the container host has at least <paramref name="size"/> free.</summary>
    public async Task EnsureHostSpaceAsync(ByteSize size, CancellationToken cancellationToken)
    {
        var free = await FreeBytesAsync(cancellationToken).ConfigureAwait(false);
        if (free < size.Bytes)
        {
            throw new SqlBranchException(ErrorCodes.HostDiskFull,
                $"The managed pool needs {size.ToGigabytes()} free on the container host, but only {new ByteSize(Math.Max(free, 1)).ToGigabytes()} is available. " +
                "Free some space or lower poolSize.");
        }
    }

    /// <summary>True when a helper can create and delete a file under the managed folder.</summary>
    public async Task<bool> IsWritableAsync(CancellationToken cancellationToken)
    {
        var result = await RunHelperAsync("writable", ManagedPoolStep.Inspect, privileged: false, hostPid: false,
            $"t='{_paths.Root}/.sqlbranch-doctor'; if touch \"$t\" && rm -f \"$t\"; then echo SQLBRANCH_WRITABLE=yes; else echo SQLBRANCH_WRITABLE=no; fi",
            cancellationToken).ConfigureAwait(false);
        return result.StdOut.Contains("SQLBRANCH_WRITABLE=yes", StringComparison.Ordinal);
    }

    public async Task<bool> FileExistsAsync(CancellationToken cancellationToken)
    {
        var result = await RunHelperAsync("stat", ManagedPoolStep.Inspect, privileged: false, hostPid: false,
            $"if [ -e '{_paths.PoolFile}' ]; then echo SQLBRANCH_FILE=yes; else echo SQLBRANCH_FILE=no; fi", cancellationToken).ConfigureAwait(false);
        return result.StdOut.Contains("SQLBRANCH_FILE=yes", StringComparison.Ordinal);
    }

    /// <summary>The size in bytes of the existing pool file.</summary>
    public async Task<long> FileSizeAsync(CancellationToken cancellationToken)
    {
        var result = await RunHelperAsync("size", ManagedPoolStep.Inspect, privileged: false, hostPid: false,
            $"stat -c %s '{_paths.PoolFile}'", cancellationToken).ConfigureAwait(false);
        return long.TryParse(result.LastLine, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? size
            : throw new ManagedPoolException(ManagedPoolStep.Inspect, $"Could not read the size of {_paths.PoolFile}.");
    }

    /// <summary>True when the pool file is mounted at the mount point, as the host's own mount table says.</summary>
    public async Task<bool> IsMountedAsync(CancellationToken cancellationToken)
    {
        var result = await RunHelperAsync("mounted", ManagedPoolStep.Inspect, privileged: true, hostPid: true,
            $"if grep -q ' {_paths.MountPoint} ' /proc/1/mountinfo; then echo SQLBRANCH_MOUNT=yes; else echo SQLBRANCH_MOUNT=no; fi", cancellationToken)
            .ConfigureAwait(false);
        return result.StdOut.Contains("SQLBRANCH_MOUNT=yes", StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates the pool file and mounts it. When a step fails, whatever this
    /// call created is removed before the exception is thrown. The caller checks
    /// host space first and prints <see cref="DescribePlan"/>.
    /// </summary>
    public async Task CreateAsync(ByteSize size, Action<string>? report, CancellationToken cancellationToken)
    {
        foreach (var line in DescribePlan(size))
        {
            report?.Invoke(line);
        }

        if (await FileExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException($"The pool file {_paths.PoolFile} already exists, so SqlBranch will not create it again.");
        }

        var createdFile = false;
        var mounted = false;
        try
        {
            // The file counts as created by this run only once the script got past its existence check
            // and the install. A file that was already there (exit 66) is never ours to delete.
            try
            {
                await RunHelperAsync("format", ManagedPoolStep.Format, privileged: true, hostPid: false,
                    $"if [ -e '{_paths.PoolFile}' ]; then exit {ExitFileExists}; fi; " +
                    "apt-get update -qq >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xfsprogs >/dev/null " +
                    $"|| exit {ExitBeforeCreate}; " +
                    $"truncate -s {size.Bytes.ToString(CultureInfo.InvariantCulture)} '{_paths.PoolFile}'; " +
                    $"mkfs.xfs -q -m reflink=1 '{_paths.PoolFile}'", cancellationToken).ConfigureAwait(false);
                createdFile = true;
            }
            catch (ManagedPoolException ex) when (ex.ExitCode == ExitFileExists)
            {
                throw new SqlBranchException($"The pool file {_paths.PoolFile} already exists, so SqlBranch will not create it again.");
            }
            catch (ManagedPoolException ex)
            {
                // A failure after the install may have left a file behind; one before it, or none at all, did not.
                createdFile = ex.ExitCode is not (ExitBeforeCreate or ExitDockerFailed or null);
                throw;
            }

            await MountAsync(cancellationToken).ConfigureAwait(false);
            mounted = true;
        }
        catch
        {
            await UndoAsync(createdFile, mounted).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Mounts the existing pool file at the mount point, in the host's own mount namespace.</summary>
    public Task MountAsync(CancellationToken cancellationToken) =>
        RunHelperAsync("mount", ManagedPoolStep.Mount, privileged: true, hostPid: true,
            $"set -e; mkdir -p '{_paths.MountPoint}'; nsenter -t 1 -m -- mount -o loop '{_paths.PoolFile}' '{_paths.MountPoint}'",
            cancellationToken);

    /// <summary>Unmounts the pool when it is mounted.</summary>
    public Task UnmountAsync(CancellationToken cancellationToken) =>
        RunHelperAsync("unmount", ManagedPoolStep.Unmount, privileged: true, hostPid: true,
            $"set -e; if grep -q ' {_paths.MountPoint} ' /proc/1/mountinfo; then nsenter -t 1 -m -- umount '{_paths.MountPoint}'; fi; " +
            // The empty mount point goes too. It has to be removed in the host's own namespace: this helper
            // still holds its own copy of the mount, which would make a plain rmdir report "busy".
            $"nsenter -t 1 -m -- rmdir '{_paths.MountPoint}' 2>/dev/null || true",
            cancellationToken);

    /// <summary>Deletes the pool file.</summary>
    public Task DeleteFileAsync(CancellationToken cancellationToken) =>
        RunHelperAsync("delete", ManagedPoolStep.Delete, privileged: false, hostPid: false,
            $"rm -f '{_paths.PoolFile}'", cancellationToken);

    /// <summary>Unmounts the pool and deletes its file, in that order; for undoing what this run created.</summary>
    public Task RemoveAsync(CancellationToken cancellationToken) => UndoAsync(createdFile: true, mounted: true);

    /// <summary>Removes what a failed create left: unmount, then delete the file. Best effort, in that order.</summary>
    private async Task UndoAsync(bool createdFile, bool mounted)
    {
        try
        {
            if (mounted || createdFile)
            {
                await UnmountAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ManagedPoolException)
        {
            // The delete below still runs; a pool file that stays mounted is reported by status.
        }

        if (createdFile)
        {
            try
            {
                await DeleteFileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ManagedPoolException)
            {
                // Nothing more to do; the original failure is what the caller sees.
            }
        }
    }

    private async Task<ContainerResult> RunHelperAsync(
        string label,
        ManagedPoolStep step,
        bool privileged,
        bool hostPid,
        string script,
        CancellationToken cancellationToken)
    {
        var name = $"sqlbranch-helper-{label}-{Guid.NewGuid().ToString("N")[..8]}";
        var spec = new ContainerSpec
        {
            Name = name,
            Image = _helperImage,
            Privileged = privileged,
            HostPid = hostPid,
            RemoveWhenDone = true,
            Mounts = [new MountSpec(MountKind.Bind, _paths.Root, _paths.Root)],
            Command = ["sh", "-c", script],
        };

        ContainerResult result;
        try
        {
            result = await _runtime.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _runtime.RemoveAsync(name, CancellationToken.None).ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            var detail = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {result.ExitCode}";
            throw new ManagedPoolException(step, $"the {step.ToString().ToLowerInvariant()} step failed: {detail}") { ExitCode = result.ExitCode };
        }

        return result;
    }
}

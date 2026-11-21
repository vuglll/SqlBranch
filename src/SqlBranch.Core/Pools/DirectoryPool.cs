using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Pools;

/// <summary>What is in a directory that might become a pool.</summary>
public enum DirectoryState
{
    /// <summary>Nothing in it.</summary>
    Empty,

    /// <summary>It carries the SqlBranch pool marker.</summary>
    Marked,

    /// <summary>It holds other files and no marker.</summary>
    Foreign,
}

/// <summary>The pool marker, the only thing that makes a directory a SqlBranch pool.</summary>
public static class PoolMarker
{
    public const string FileName = ".sqlbranch-pool";

    public const int Format = 1;

    public static string Path => ContainerLabels.PoolMountPath + "/" + FileName;

    /// <summary>The marker file's content.</summary>
    public static string Json(DateTimeOffset created) =>
        $"{{\"format\":{Format.ToString(CultureInfo.InvariantCulture)},\"created\":\"{created.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\"}}";
}

/// <summary>
/// A directory used as a pool: a host directory, the <c>sqlbranch-pool</c>
/// volume or the managed mount. The directory is looked at read-only first, and
/// only an empty or marked one is changed.
/// </summary>
public static class DirectoryPool
{
    /// <summary>The user and group the engine runs as inside its container.</summary>
    public const string EngineOwner = "10001:0";

    /// <summary>Reads the state of the pool directory without changing anything.</summary>
    public static async Task<DirectoryState> InspectAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        CancellationToken cancellationToken)
    {
        var script =
            $"if [ -f {PoolMarker.Path} ]; then echo SQLBRANCH_POOL=marked; " +
            "elif [ -z \"$(ls -A /pool)\" ]; then echo SQLBRANCH_POOL=empty; " +
            "else echo SQLBRANCH_POOL=foreign; fi";
        var result = await PoolWork.RunAsync(runtime, image, pool, script, readOnly: true, cancellationToken).ConfigureAwait(false);
        return result.StdOut switch
        {
            var o when o.Contains("SQLBRANCH_POOL=marked", StringComparison.Ordinal) => DirectoryState.Marked,
            var o when o.Contains("SQLBRANCH_POOL=empty", StringComparison.Ordinal) => DirectoryState.Empty,
            var o when o.Contains("SQLBRANCH_POOL=foreign", StringComparison.Ordinal) => DirectoryState.Foreign,
            _ => throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Could not look at the pool {pool} (exit code {result.ExitCode}): {FirstLine(result.StdErr)}"),
        };
    }

    /// <summary>
    /// Exit 2 for a directory that is not empty and has no marker. Nothing is
    /// changed: the check mounts the directory read-only.
    /// </summary>
    public static async Task EnsureUsableAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        CancellationToken cancellationToken)
    {
        if (await InspectAsync(runtime, image, pool, cancellationToken).ConfigureAwait(false) == DirectoryState.Foreign)
        {
            throw new ConfigException(
                $"{pool} is not empty and is not a SqlBranch pool, so SqlBranch will not use it or change it. " +
                "Point 'pool' at an empty directory or at one SqlBranch created.",
                null, SettingKeys.Pool);
        }
    }

    /// <summary>
    /// Writes the marker if it is missing and makes the engine's user the owner,
    /// after checking that the directory is empty or already a pool. Returns
    /// true when this call wrote the marker.
    /// </summary>
    public static async Task<bool> InitializeAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureUsableAsync(runtime, image, pool, cancellationToken).ConfigureAwait(false);

        var script =
            $"set -e; [ -f {PoolMarker.Path} ] || {{ printf '%s' '{PoolMarker.Json(now)}' > {PoolMarker.Path}; echo SQLBRANCH_MARKER=new; }}; " +
            $"chown -R {EngineOwner} /pool";
        var result = await PoolWork.RunAsync(runtime, image, pool, script, readOnly: false, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Could not prepare the pool {pool} (exit code {result.ExitCode}): {FirstLine(result.StdErr)}");
        }

        return result.StdOut.Contains("SQLBRANCH_MARKER=new", StringComparison.Ordinal);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
}

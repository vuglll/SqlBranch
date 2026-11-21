using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Pools;

/// <summary>What setting the copy-on-write hint did.</summary>
public enum CowHintResult
{
    /// <summary>The hint is set on the branches folder.</summary>
    Set,

    /// <summary>The file system or the tools do not allow the hint; the folder exists without it.</summary>
    Unsupported,
}

/// <summary>
/// The 8 KiB copy-on-write extent hint on <c>/pool/branches</c> (design D14).
/// By default XFS unshares 128 KiB around each written 8 KiB page; with the hint
/// a branch grows by about half as much. The folder is created for the engine's
/// user, and files cloned into it inherit the hint. The script runs in an
/// unprivileged work container that mounts only the pool; its image is Ubuntu
/// because the SQL Server image has no <c>xfs_io</c>.
/// </summary>
public static class CowHint
{
    public const string Size = "8k";

    public const long Bytes = 8192;

    public const string BranchesPath = ContainerLabels.PoolMountPath + "/branches";

    /// <summary>Creates the folder, hands it to the engine's user and sets the hint. Prints one tagged line.</summary>
    public static string SetScript { get; } =
        $"set -e; if [ -d {BranchesPath} ]; then echo SQLBRANCH_BRANCHES=existing; else mkdir -p {BranchesPath} && chown {DirectoryPool.EngineOwner} {BranchesPath} && echo SQLBRANCH_BRANCHES=new; fi; " +
        "command -v xfs_io >/dev/null 2>&1 || { apt-get update -qq >/dev/null 2>&1 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xfsprogs >/dev/null 2>&1; } || true; " +
        $"if xfs_io -c 'cowextsize {Size}' {BranchesPath} >/dev/null 2>&1; then echo SQLBRANCH_COWHINT=set; else echo SQLBRANCH_COWHINT=unsupported; fi";

    /// <summary>Prints the hint in bytes as <c>SQLBRANCH_COWSIZE=n</c>.</summary>
    public static string ReadScript { get; } =
        "command -v xfs_io >/dev/null 2>&1 || { apt-get update -qq >/dev/null 2>&1 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xfsprogs >/dev/null 2>&1; } || true; " +
        $"echo SQLBRANCH_COWSIZE=$(xfs_io -c cowextsize {BranchesPath} | sed -n 's/^\\[\\([0-9]*\\)\\].*/\\1/p')";

    /// <summary>The result of <see cref="SetAsync"/>.</summary>
    /// <param name="Result">Whether the hint was set.</param>
    /// <param name="CreatedFolder">This call created the branches folder.</param>
    public sealed record Outcome(CowHintResult Result, bool CreatedFolder);

    /// <summary>
    /// Creates <c>/pool/branches</c> and sets the hint. A pool that cannot take the hint
    /// is not an error: the folder exists and the caller reports <see cref="CowHintResult.Unsupported"/>.
    /// </summary>
    public static async Task<Outcome> SetAsync(IContainerRuntime runtime, PoolLocation pool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var result = await PoolWork.RunAsync(runtime, ManagedPool.HelperImage, pool, SetScript, readOnly: false, cancellationToken).ConfigureAwait(false);
        var created = result.StdOut.Contains("SQLBRANCH_BRANCHES=new", StringComparison.Ordinal);
        var set = result.Succeeded && result.StdOut.Contains("SQLBRANCH_COWHINT=set", StringComparison.Ordinal);
        return new Outcome(set ? CowHintResult.Set : CowHintResult.Unsupported, created);
    }
}

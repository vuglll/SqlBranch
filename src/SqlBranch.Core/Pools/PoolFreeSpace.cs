using System.Globalization;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Pools;

/// <summary>
/// The pool's free space, read from inside the engine container. Image builds, copy-mode branch creates and branch
/// resets all check it before they write a file, so a full pool fails early with exit 1 <c>pool_full</c> that names
/// the space needed and the space available, rather than half way through a copy.
/// </summary>
public static class PoolFreeSpace
{
    private const string FreeTag = "SQLBRANCH_FREE=";

    /// <summary>The free bytes of the pool, as <c>df</c> reports them to the engine's user.</summary>
    public static async Task<long> FreeBytesAsync(IContainerRuntime runtime, string engineName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var result = await runtime.ExecAsync(
            engineName, ["sh", "-c", $"echo {FreeTag}$(df -B1 --output=avail /pool | tail -n 1 | tr -d ' ')"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            throw new SqlBranchException(
                $"The engine could not do this step (reading the pool's free space), exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}{(detail is null ? string.Empty : ": " + detail)}.");
        }

        var value = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(FreeTag, StringComparison.Ordinal))?[FreeTag.Length..];
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var free))
        {
            throw new SqlBranchException("Could not read the pool's free space.");
        }

        return free;
    }

    /// <summary>
    /// Throws exit 1 <c>pool_full</c> unless at least <paramref name="needed"/> bytes are free. <paramref name="what"/>
    /// completes "The pool is too full for ...". Call it before the first file is made.
    /// </summary>
    public static async Task RequireAsync(IContainerRuntime runtime, string engineName, long needed, string what, CancellationToken cancellationToken)
    {
        var free = await FreeBytesAsync(runtime, engineName, cancellationToken).ConfigureAwait(false);
        if (free < needed)
        {
            throw new SqlBranchException(ErrorCodes.PoolFull,
                $"The pool is too full for {what}: it needs {new ByteSize(needed).ToBinary()} and {new ByteSize(free).ToBinary()} is available.");
        }
    }
}

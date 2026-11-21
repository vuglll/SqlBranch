using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Pools;

/// <summary>
/// Decides the clone mode by trying it: write a 1 MiB file in the pool and
/// attempt a block-sharing copy. Success is <c>thin</c>, failure is <c>copy</c>.
/// </summary>
public static class CloneProbe
{
    /// <summary>The probe folder the commands share. It is removed again by the probe.</summary>
    public const string Directory = "/pool/.probe";

    /// <summary>The probe, a fixed script. It leaves nothing behind.</summary>
    public static string Script { get; } = ScriptFor(Directory);

    /// <summary>
    /// The probe working in <paramref name="directory"/>. Two commands that probe at the same time must use different
    /// folders, because each removes its folder when it is done; <c>branch create</c> does, so concurrent creates do not
    /// break each other's probe. <paramref name="directory"/> is built by the caller from a fixed prefix and a random id.
    /// </summary>
    public static string ScriptFor(string directory) =>
        $"mkdir -p {directory} && " +
        $"dd if=/dev/zero of={directory}/a bs=1048576 count=1 2>/dev/null && " +
        $"if cp --reflink=always {directory}/a {directory}/b 2>/dev/null; then echo SQLBRANCH_PROBE=thin; else echo SQLBRANCH_PROBE=copy; fi; " +
        $"rm -rf {directory}";

    /// <summary>Probes a pool from a short-lived work container.</summary>
    public static async Task<CloneMode> ProbeAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        CancellationToken cancellationToken)
    {
        var result = await PoolWork.RunAsync(runtime, image, pool, Script, readOnly: false, cancellationToken).ConfigureAwait(false);
        return Parse(result, pool.ToString());
    }

    /// <summary>Probes the pool the running engine has mounted. <paramref name="uniqueId"/> (letters and digits) gives the probe a folder of its own.</summary>
    public static async Task<CloneMode> ProbeInEngineAsync(
        IContainerRuntime runtime,
        CancellationToken cancellationToken,
        string engineName = ContainerLabels.EngineName,
        string? uniqueId = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (uniqueId is not null && !uniqueId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The probe id must be letters and digits.", nameof(uniqueId));
        }

        var script = uniqueId is null ? Script : ScriptFor($"{Directory}-{uniqueId}");
        var result = await runtime.ExecAsync(engineName, ["sh", "-c", script], cancellationToken).ConfigureAwait(false);
        return Parse(result, "the engine's pool");
    }

    private static CloneMode Parse(ContainerResult result, string where)
    {
        if (result.StdOut.Contains("SQLBRANCH_PROBE=thin", StringComparison.Ordinal))
        {
            return CloneMode.Thin;
        }

        if (result.StdOut.Contains("SQLBRANCH_PROBE=copy", StringComparison.Ordinal))
        {
            return CloneMode.Copy;
        }

        var detail = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        throw new SqlBranchException(ErrorCodes.OperationFailed,
            $"Could not test whether {where} can share blocks (exit code {result.ExitCode}){(detail is null ? "." : ": " + detail)}");
    }
}

using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Engine;

/// <summary>
/// The engine's <c>sa</c> password, stored in the pool at
/// <c>/pool/engine/credentials.json</c> with owner-only access. Reads and writes
/// go through a short-lived work container, and the password reaches it on
/// standard input, never on a command line and never in an environment variable
/// (a container's environment is visible in <c>docker inspect</c> while it lives).
/// </summary>
public static class EngineCredentials
{
    public const string Directory = ContainerLabels.PoolMountPath + "/engine";

    public const string Path = Directory + "/credentials.json";

    private const string Prefix = "SQLBRANCH_CREDENTIALS=";

    /// <summary>The stored password, or null when the pool has no credentials file.</summary>
    public static async Task<string?> ReadAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        CancellationToken cancellationToken)
    {
        // The file travels as base64. The runtime masks every registered secret in what a container prints,
        // so the plain JSON would come back with the password replaced by ***.
        var script = $"if [ -f {Path} ]; then echo {Prefix}$(base64 -w0 {Path}); else echo {Prefix}none; fi";
        var result = await PoolWork.RunAsync(runtime, image, pool, script, readOnly: true, cancellationToken).ConfigureAwait(false);
        var line = result.Succeeded
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal))
            : null;
        if (line is null)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Could not read the engine credentials from {pool} (exit code {result.ExitCode}).");
        }

        var encoded = line[Prefix.Length..];
        if (encoded == "none")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            return document.RootElement.GetProperty("password").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The engine credentials file in {pool} is not valid.");
        }
    }

    /// <summary>Writes the file readable and writable by the engine's user only.</summary>
    public static async Task WriteAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        string password,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { user = "sa", password });
        var script =
            $"set -e; umask 077; cat > {Path}; " +
            $"chown {DirectoryPool.EngineOwner} {Path}; chmod 600 {Path}";
        var result = await PoolWork.RunAsync(runtime, image, pool, script, readOnly: false, cancellationToken, stdin: json).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Could not write the engine credentials into {pool} (exit code {result.ExitCode}).");
        }
    }
}

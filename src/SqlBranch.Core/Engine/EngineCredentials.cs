using System.Text;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Images;
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

    /// <summary>The newest credentials format this release reads and writes.</summary>
    public const int NewestFormat = 2;

    /// <summary>The name of the temporary file the format-2 write goes through (design D2, D11).</summary>
    public const string TemporaryName = "credentials.json.new";

    /// <summary>The temporary file's path.</summary>
    public const string TemporaryPath = Directory + "/" + TemporaryName;

    /// <summary>The stored password, or null when the pool has no credentials file.</summary>
    public static async Task<string?> ReadAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        CancellationToken cancellationToken) =>
        (await ReadFileAsync(runtime, image, pool, cancellationToken).ConfigureAwait(false))?.Password;

    /// <summary>
    /// The whole credentials file in either format, or null when the pool has none. A format above
    /// <see cref="NewestFormat"/> is refused with <c>operation_failed</c> before anything else reads the pool.
    /// </summary>
    public static async Task<CredentialsFile?> ReadFileAsync(
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

        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The engine credentials file in {pool} is not valid.");
        }

        return CredentialsFile.Parse(json, pool.ToString());
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

    /// <summary>
    /// The shell script of the format-2 write (design D2): a temporary file next to the old one, owner and mode, a
    /// flush, a rename over <c>credentials.json</c> and a flush of the directory. It reads the JSON from standard input,
    /// so the password is never in an argument or the environment. When a step before the rename fails it deletes its own
    /// temporary file, leaves the old file as it was and exits 1. It runs as root, because the engine image's own user
    /// cannot change ownership. <paramref name="directory"/> is the pool's engine folder; tests pass a temporary one.
    /// </summary>
    internal static string RewriteScript(string directory = Directory, string owner = DirectoryPool.EngineOwner)
    {
        var target = Quoting.Shell(directory + "/credentials.json");
        var temp = Quoting.Shell(directory + "/" + TemporaryName);
        return
            $"umask 077; rm -f {temp}; " +
            $"if cat > {temp} && chown {owner} {temp} && chmod 600 {temp} && sync {temp} && mv -f {temp} {target}; " +
            $"then sync {Quoting.Shell(directory)} || true; else rm -f {temp}; exit 1; fi";
    }

    /// <summary>
    /// Writes a format-2 file through <see cref="RewriteScript"/>. A failure before the rename leaves the old file
    /// unchanged and no temporary file behind, and the pool stays in the format it had; a full pool is
    /// <c>pool_full</c>.
    /// </summary>
    public static async Task WriteFileAsync(
        IContainerRuntime runtime,
        string image,
        PoolLocation pool,
        CredentialsFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var result = await PoolWork.RunAsync(runtime, image, pool, RewriteScript(), readOnly: false, cancellationToken, stdin: file.ToJson()).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var full = result.StdErr.Contains("No space left", StringComparison.OrdinalIgnoreCase);
            throw new SqlBranchException(full ? ErrorCodes.PoolFull : ErrorCodes.OperationFailed,
                $"Could not write the engine credentials into {pool} (exit code {result.ExitCode}). The old credentials file is unchanged.");
        }
    }
}

/// <summary>The upgrade record of a format-2 credentials file.</summary>
/// <param name="StartedAt">When the upgrade started.</param>
/// <param name="State"><c>running</c> or <c>done</c>.</param>
public sealed record UpgradeRecord(DateTimeOffset StartedAt, string State)
{
    public const string Running = "running";

    public const string Done = "done";
}

/// <summary>
/// The engine credentials file in memory. Format 1 is <c>{"user","password"}</c>; format 2 nests the login under
/// <c>engine</c> and adds <c>upgrade</c> and <c>note</c> (design D2). A 0.2.2 reader finds no top-level password in
/// format 2 and stops, which is the fence.
/// </summary>
public sealed record CredentialsFile(int Format, string User, string Password, UpgradeRecord? Upgrade = null, string? Note = null)
{
    public const string DefaultNote =
        "Written by SqlBranch 0.3 (pool format 2). SqlBranch 0.2.x tools and SqlBranch.Testing 0.2.x cannot read this file; update both.";

    /// <summary>A format-2 file.</summary>
    public static CredentialsFile Format2(string password, UpgradeRecord? upgrade, string? note = DefaultNote) =>
        new(2, "sa", password, upgrade, note);

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (Format >= 2)
            {
                writer.WriteNumber("format", Format);
                writer.WriteStartObject("engine");
                writer.WriteString("user", User);
                writer.WriteString("password", Password);
                writer.WriteEndObject();
                if (Upgrade is { } upgrade)
                {
                    writer.WriteStartObject("upgrade");
                    writer.WriteString("startedAt", upgrade.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteString("state", upgrade.State);
                    writer.WriteEndObject();
                }

                if (Note is not null)
                {
                    writer.WriteString("note", Note);
                }
            }
            else
            {
                writer.WriteString("user", User);
                writer.WriteString("password", Password);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static CredentialsFile Parse(string json, string pool)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var format = root.TryGetProperty("format", out var f) ? f.GetInt32() : 1;
            if (format > EngineCredentials.NewestFormat)
            {
                throw new SqlBranchException(ErrorCodes.OperationFailed,
                    $"The pool {pool} uses format {format.ToString(System.Globalization.CultureInfo.InvariantCulture)}, which this sqlbranch does not read. " +
                    "Update the sqlbranch tool and the SqlBranch.Testing package.");
            }

            if (format < 2)
            {
                return new CredentialsFile(1, root.TryGetProperty("user", out var u) ? u.GetString() ?? "sa" : "sa",
                    root.GetProperty("password").GetString() ?? throw new InvalidOperationException());
            }

            var engine = root.GetProperty("engine");
            UpgradeRecord? upgrade = null;
            if (root.TryGetProperty("upgrade", out var up) && up.ValueKind == JsonValueKind.Object)
            {
                upgrade = new UpgradeRecord(
                    DateTimeOffset.Parse(up.GetProperty("startedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
                    up.GetProperty("state").GetString() ?? throw new InvalidOperationException());
            }

            return new CredentialsFile(format, engine.TryGetProperty("user", out var eu) ? eu.GetString() ?? "sa" : "sa",
                engine.GetProperty("password").GetString() ?? throw new InvalidOperationException(),
                upgrade, root.TryGetProperty("note", out var n) ? n.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The engine credentials file in {pool} is not valid.");
        }
    }
}

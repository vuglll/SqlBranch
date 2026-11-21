namespace SqlBranch.Core.Containers;

/// <summary>How a mount reaches the container.</summary>
public enum MountKind
{
    /// <summary>A directory on the container host.</summary>
    Bind,

    /// <summary>A Docker volume.</summary>
    Volume,
}

/// <summary>One mount into a container.</summary>
/// <param name="Kind">Bind or volume.</param>
/// <param name="Source">The host path or the volume name.</param>
/// <param name="Target">The path inside the container.</param>
/// <param name="ReadOnly">Mount read-only.</param>
/// <param name="SubPath">A folder inside the source to mount instead of its root.</param>
public sealed record MountSpec(MountKind Kind, string Source, string Target, bool ReadOnly = false, string? SubPath = null);

/// <summary>A port published on the container host.</summary>
public sealed record PortSpec(string HostAddress, int HostPort, int ContainerPort);

/// <summary>
/// A container to create. The runtime adds the <c>io.sqlbranch.managed=true</c>
/// label itself, so a caller cannot forget it.
/// </summary>
public sealed record ContainerSpec
{
    public required string Name { get; init; }

    public required string Image { get; init; }

    /// <summary>The command and its arguments; empty for the image's own command.</summary>
    public IReadOnlyList<string> Command { get; init; } = [];

    /// <summary>Extra labels. The managed label is always added.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Environment variables. Values travel in the docker process's environment,
    /// never on its command line.
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Text for the container's standard input (<c>docker run -i</c>). Unlike an environment
    /// variable it is not stored in the container's configuration, so <c>docker inspect</c> never shows it.
    /// </summary>
    public string? Stdin { get; init; }

    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];

    public IReadOnlyList<PortSpec> Ports { get; init; } = [];

    /// <summary>Only the one-off helpers that format and mount a managed pool set this.</summary>
    public bool Privileged { get; init; }

    /// <summary>Share the host's process namespace; only the mount helper sets this.</summary>
    public bool HostPid { get; init; }

    /// <summary>Remove the container when it ends (<c>--rm</c>).</summary>
    public bool RemoveWhenDone { get; init; }

    /// <summary>Start in the background and return at once (<c>-d</c>).</summary>
    public bool Detach { get; init; }

    public string? User { get; init; }

    /// <summary>Replaces the image's entrypoint; an empty string clears it. Null keeps the image's own.</summary>
    public string? Entrypoint { get; init; }

    /// <summary>The platform to run, for example <c>linux/amd64</c>; null for the host's own.</summary>
    public string? Platform { get; init; }
}

/// <summary>The platform the SQL Server image needs, whatever the host is.</summary>
public static class ImagePlatform
{
    public const string Amd64 = "linux/amd64";

    /// <summary>SQL Server's Linux image exists for x86-64 only, so other hosts run it under emulation.</summary>
    public static string? For(string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.StartsWith("mcr.microsoft.com/mssql/server", StringComparison.OrdinalIgnoreCase) ? Amd64 : null;
    }
}

/// <summary>The outcome of a command run in or for a container.</summary>
public sealed record ContainerResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The last non-empty line of standard output.</summary>
    public string LastLine => StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
}

/// <summary>What <c>docker inspect</c> says about a container.</summary>
public sealed record ContainerInfo(
    string Name,
    string Image,
    bool Running,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> PublishedPorts)
{
    public string? Id { get; init; }

    public string? Hostname { get; init; }

    public bool IsManaged => Labels.TryGetValue(ContainerLabels.Managed, out var value) && value == "true";
}

/// <summary>Facts about the container host.</summary>
public sealed record RuntimeInfo(string OperatingSystem, string Architecture, string Name)
{
    public bool IsDockerDesktop => OperatingSystem.Contains("Docker Desktop", StringComparison.OrdinalIgnoreCase);
}

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Containers;

/// <summary>
/// <see cref="IContainerRuntime"/> over the <c>docker</c> command. Everything it
/// creates is labeled <c>io.sqlbranch.managed=true</c>, and it refuses to exec
/// into, start, stop, remove or read the log of a container without that label.
/// </summary>
public sealed class DockerRuntime : IContainerRuntime
{
    private readonly IProcessRunner _runner;
    private readonly SecretRedactor _redactor;
    private readonly string _docker;

    public DockerRuntime(IProcessRunner runner, SecretRedactor redactor, string dockerCommand = "docker")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _docker = dockerCommand;
    }

    /// <summary>The arguments of <c>docker run</c> for a spec. Public so tests can check the command line.</summary>
    public static IReadOnlyList<string> BuildRunArguments(ContainerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var args = new List<string> { "run" };
        if (spec.Detach)
        {
            args.Add("-d");
        }

        if (spec.RemoveWhenDone)
        {
            args.Add("--rm");
        }

        if (spec.Stdin is not null)
        {
            args.Add("-i");
        }

        args.AddRange(["--name", spec.Name]);

        if (spec.Platform is not null)
        {
            args.AddRange(["--platform", spec.Platform]);
        }

        var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in spec.Labels)
        {
            labels[key] = value;
        }

        labels[ContainerLabels.Managed] = "true";
        foreach (var (key, value) in labels)
        {
            args.AddRange(["--label", $"{key}={value}"]);
        }

        if (spec.Privileged)
        {
            args.Add("--privileged");
        }

        if (spec.HostPid)
        {
            args.AddRange(["--pid", "host"]);
        }

        if (spec.User is not null)
        {
            args.AddRange(["--user", spec.User]);
        }

        if (spec.Entrypoint is not null)
        {
            args.AddRange(["--entrypoint", spec.Entrypoint]);
        }

        foreach (var key in spec.Env.Keys.Order(StringComparer.Ordinal))
        {
            args.AddRange(["-e", key]);
        }

        foreach (var port in spec.Ports)
        {
            args.AddRange(["-p", $"{port.HostAddress}:{port.HostPort.ToString(CultureInfo.InvariantCulture)}:{port.ContainerPort.ToString(CultureInfo.InvariantCulture)}"]);
        }

        foreach (var mount in spec.Mounts)
        {
            if (mount.SubPath is not null && mount.Kind == MountKind.Volume)
            {
                // -v cannot name a folder inside a volume; --mount can (Docker 26 and later).
                args.AddRange(["--mount", $"type=volume,src={mount.Source},dst={mount.Target},volume-subpath={mount.SubPath}{(mount.ReadOnly ? ",readonly" : string.Empty)}"]);
                continue;
            }

            var source = mount.SubPath is null ? mount.Source : $"{mount.Source.TrimEnd('/')}/{mount.SubPath}";
            args.AddRange(["-v", $"{source}:{mount.Target}{(mount.ReadOnly ? ":ro" : string.Empty)}"]);
        }

        args.Add(spec.Image);
        args.AddRange(spec.Command);
        return args;
    }

    public async Task<RuntimeInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["info", "--format", "{{.OperatingSystem}}|{{.Architecture}}|{{.Name}}"], null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failure("docker info", result);
        }

        var parts = result.StdOut.Trim().Split('|');
        return new RuntimeInfo(parts.ElementAtOrDefault(0) ?? string.Empty, parts.ElementAtOrDefault(1) ?? string.Empty, parts.ElementAtOrDefault(2) ?? string.Empty);
    }

    public async Task<ContainerResult> RunAsync(ContainerSpec spec, CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(BuildRunArguments(spec), spec.Env, cancellationToken, spec.Stdin).ConfigureAwait(false);
        if (spec.Detach && !result.Succeeded)
        {
            throw Failure("docker run", result);
        }

        return result;
    }

    public async Task<ContainerResult> ExecAsync(string container, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RequireManagedAsync(container, cancellationToken).ConfigureAwait(false);
        return await RunDockerAsync(["exec", container, .. command], null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IStreamingProcess> ExecStreamAsync(string container, IReadOnlyList<string> command, StreamDirection direction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RequireManagedAsync(container, cancellationToken).ConfigureAwait(false);
        string[] args = direction == StreamDirection.Write ? ["exec", "-i", container, .. command] : ["exec", container, .. command];
        try
        {
            return _runner.Start(_docker, args, null, direction, cancellationToken);
        }
        catch (Win32Exception ex)
        {
            throw new SqlBranchException(ErrorCodes.DockerUnavailable,
                $"The '{_docker}' command was not found. Install Docker and make sure '{_docker}' is on the PATH. ({_redactor.Redact(ex.Message)})");
        }
    }

    /// <summary>Hands a copied-in file to the engine's user, only when it is a regular file and not a link.</summary>
    internal const string FixOwnerScript = "[ -f \"$2\" ] && [ ! -L \"$2\" ] && chown \"$1\" \"$2\" && chmod 640 \"$2\"";

    public async Task CopyToAsync(string container, string hostPath, string containerPath, string owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostPath);
        ArgumentException.ThrowIfNullOrEmpty(containerPath);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        await RequireManagedAsync(container, cancellationToken).ConfigureAwait(false);

        var copy = await RunDockerAsync(["cp", "-L", hostPath, $"{container}:{containerPath}"], null, cancellationToken).ConfigureAwait(false);
        if (!copy.Succeeded)
        {
            throw Failure("docker cp", copy);
        }

        // docker cp keeps the client's uid and mode. Fix both as root, so the engine's own user can read the file.
        // -L copied what a symlink points to, so the target is a regular file; the guard refuses anything else, because
        // root's chown and chmod would follow a link to a file the client chose inside the container.
        var fix = await RunDockerAsync(["exec", "--user", "0", container, "sh", "-c", FixOwnerScript, "sh", owner, containerPath], null, cancellationToken)
            .ConfigureAwait(false);
        if (!fix.Succeeded)
        {
            throw Failure("docker exec (chown of the copied file)", fix);
        }
    }

    public async Task<ContainerInfo?> InspectAsync(string name, CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["inspect", "--type", "container", name], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.StdErr.Contains("No such", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw Failure("docker inspect", result);
        }

        return ParseInspect(result.StdOut);
    }

    public async Task StartAsync(string name, CancellationToken cancellationToken)
    {
        await RequireManagedAsync(name, cancellationToken).ConfigureAwait(false);
        var result = await RunDockerAsync(["start", name], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failure("docker start", result);
        }
    }

    public async Task StopAsync(string name, CancellationToken cancellationToken)
    {
        await RequireManagedAsync(name, cancellationToken).ConfigureAwait(false);
        var result = await RunDockerAsync(["stop", name], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failure("docker stop", result);
        }
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var info = await InspectAsync(name, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return false;
        }

        RequireLabel(info);
        var result = await RunDockerAsync(["rm", "--force", name], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !result.StdErr.Contains("No such", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("docker rm", result);
        }

        return true;
    }

    public async Task<string> LogsAsync(string name, int tail, CancellationToken cancellationToken)
    {
        await RequireManagedAsync(name, cancellationToken).ConfigureAwait(false);
        var result = await RunDockerAsync(["logs", "--tail", tail.ToString(CultureInfo.InvariantCulture), name], null, cancellationToken)
            .ConfigureAwait(false);
        return result.StdOut + result.StdErr;
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListManagedAsync(CancellationToken cancellationToken)
    {
        var listed = await RunDockerAsync(["ps", "-a", "--filter", $"label={ContainerLabels.Managed}=true", "--format", "{{.Names}}"], null, cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            throw Failure("docker ps", listed);
        }

        var found = new List<ContainerInfo>();
        foreach (var name in listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (await InspectAsync(name, cancellationToken).ConfigureAwait(false) is { } info)
            {
                found.Add(info);
            }
        }

        return found;
    }

    public async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var result = await RunDockerAsync(["image", "inspect", "--format", "{{.Id}}", image], null, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return true;
        }

        if (result.StdErr.Contains("No such", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw Failure("docker image inspect", result);
    }

    public async Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["volume", "inspect", name], null, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return true;
        }

        if (result.StdErr.Contains("No such", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw Failure("docker volume inspect", result);
    }

    public async Task<bool> VolumeIsManagedAsync(string name, CancellationToken cancellationToken)
    {
        var inspect = await RunDockerAsync(["volume", "inspect", "--format", "{{json .Labels}}", name], null, cancellationToken).ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            if (inspect.StdErr.Contains("No such", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw Failure("docker volume inspect", inspect);
        }

        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(inspect.StdOut.Trim() is "null" or "" ? "{}" : inspect.StdOut) ?? [];
        return labels.TryGetValue(ContainerLabels.Managed, out var value) && value == "true";
    }

    public async Task CreateVolumeAsync(string name, CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["volume", "create", "--label", $"{ContainerLabels.Managed}=true", name], null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failure("docker volume create", result);
        }
    }

    public async Task<bool> RemoveVolumeAsync(string name, CancellationToken cancellationToken)
    {
        var inspect = await RunDockerAsync(["volume", "inspect", "--format", "{{json .Labels}}", name], null, cancellationToken).ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            if (inspect.StdErr.Contains("No such", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw Failure("docker volume inspect", inspect);
        }

        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(inspect.StdOut.Trim() is "null" or "" ? "{}" : inspect.StdOut) ?? [];
        if (!labels.TryGetValue(ContainerLabels.Managed, out var value) || value != "true")
        {
            throw new SqlBranchException(ErrorCodes.ForeignContainer,
                $"The volume '{name}' was not created by SqlBranch, so SqlBranch will not remove it.");
        }

        var result = await RunDockerAsync(["volume", "rm", name], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failure("docker volume rm", result);
        }

        return true;
    }

    private static ContainerInfo ParseInspect(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement[0];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.GetProperty("Config").TryGetProperty("Labels", out var labelNode) && labelNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelNode.EnumerateObject())
            {
                labels[label.Name] = label.Value.GetString() ?? string.Empty;
            }
        }

        var ports = new List<string>();
        if (node.TryGetProperty("NetworkSettings", out var network) &&
            network.TryGetProperty("Ports", out var portNode) && portNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var port in portNode.EnumerateObject())
            {
                if (port.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var binding in port.Value.EnumerateArray())
                {
                    ports.Add($"{binding.GetProperty("HostIp").GetString()}:{binding.GetProperty("HostPort").GetString()}");
                }
            }
        }

        return new ContainerInfo(
            node.GetProperty("Name").GetString()!.TrimStart('/'),
            node.GetProperty("Config").GetProperty("Image").GetString() ?? string.Empty,
            node.GetProperty("State").GetProperty("Running").GetBoolean(),
            labels,
            ports)
        {
            Id = node.TryGetProperty("Id", out var id) ? id.GetString() : null,
            Hostname = node.GetProperty("Config").TryGetProperty("Hostname", out var hostname) ? hostname.GetString() : null,
        };
    }

    private async Task RequireManagedAsync(string name, CancellationToken cancellationToken)
    {
        var info = await InspectAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException($"The container '{name}' does not exist.");
        RequireLabel(info);
    }

    private static void RequireLabel(ContainerInfo info)
    {
        if (!info.IsManaged)
        {
            throw new SqlBranchException(ErrorCodes.ForeignContainer,
                $"The container '{info.Name}' was not created by SqlBranch (it has no {ContainerLabels.Managed}=true label), so SqlBranch will not touch it.");
        }
    }

    private async Task<ContainerResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        ProcessOutput output;
        try
        {
            output = await _runner.RunAsync(_docker, arguments, environment, cancellationToken, standardInput).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw new SqlBranchException(ErrorCodes.DockerUnavailable,
                $"The '{_docker}' command was not found. Install Docker and make sure '{_docker}' is on the PATH. ({_redactor.Redact(ex.Message)})");
        }

        if (output.ExitCode != 0 && IsDaemonUnreachable(output.StdErr))
        {
            throw new SqlBranchException(ErrorCodes.DockerUnavailable,
                $"Docker is installed but its daemon is not reachable. Start Docker and try again. ({_redactor.Redact(FirstLine(output.StdErr))})");
        }

        return new ContainerResult(output.ExitCode, _redactor.Redact(output.StdOut), _redactor.Redact(output.StdErr));
    }

    private static bool IsDaemonUnreachable(string stderr) =>
        stderr.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("failed to connect to the docker API", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("error during connect", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static SqlBranchException Failure(string what, ContainerResult result) =>
        new($"{what} failed with exit code {result.ExitCode}: {FirstLine(result.StdErr)}");
}

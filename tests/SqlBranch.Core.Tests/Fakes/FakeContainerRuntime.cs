using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// A container runtime with no Docker behind it. It keeps the containers and
/// volumes it was asked to create, applies the same label rule as the real
/// runtime, and answers <c>run</c> and <c>exec</c> from scripts.
/// </summary>
internal sealed class FakeContainerRuntime : IContainerRuntime
{
    public RuntimeInfo Info { get; set; } = new("Ubuntu 24.04", "x86_64", "host");

    /// <summary>Containers that exist now, by name. Add unlabeled ones to test refusal.</summary>
    public Dictionary<string, ContainerInfo> Containers { get; } = [];

    public HashSet<string> Volumes { get; } = [];

    /// <summary>Volumes in <see cref="Volumes"/> that do not carry the managed label, as a user's own volume.</summary>
    public HashSet<string> UnlabeledVolumes { get; } = [];

    /// <summary>Images that are on the host. Empty means every image is.</summary>
    public HashSet<string> Images { get; } = [];

    /// <summary>Every <c>run</c> in order, including containers that removed themselves.</summary>
    public List<ContainerSpec> Runs { get; } = [];

    /// <summary>Every call as a short line, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>When set, <c>info</c> throws it, as the real runtime does when Docker is missing.</summary>
    public Exception? InfoFailure { get; set; }

    /// <summary>What <c>logs</c> returns for every container.</summary>
    public string Log { get; set; } = string.Empty;

    /// <summary>Containers whose removal fails, as when the daemon refuses.</summary>
    public HashSet<string> FailRemove { get; } = [];

    /// <summary>Volumes whose removal fails.</summary>
    public HashSet<string> FailRemoveVolume { get; } = [];

    public Func<ContainerSpec, ContainerResult> RunHandler { get; set; } = _ => Ok();

    public Func<string, IReadOnlyList<string>, ContainerResult> ExecHandler { get; set; } = (_, _) => Ok();

    public static ContainerResult Ok(string stdout = "") => new(0, stdout, string.Empty);

    public static ContainerResult Fail(int code = 1, string stderr = "failed") => new(code, string.Empty, stderr);

    /// <summary>The script a <c>sh -c</c> run was given, or an empty string.</summary>
    public static string ScriptOf(ContainerSpec spec) =>
        spec.Command.Count >= 3 && spec.Command[0] == "sh" && spec.Command[1] == "-c" ? spec.Command[2] : string.Join(' ', spec.Command);

    public Task<RuntimeInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        Calls.Add("info");
        if (InfoFailure is not null)
        {
            throw InfoFailure;
        }

        return Task.FromResult(Info);
    }

    public Task<ContainerResult> RunAsync(ContainerSpec spec, CancellationToken cancellationToken)
    {
        Runs.Add(spec);
        Calls.Add($"run {spec.Name}");
        var result = RunHandler(spec);
        if (!spec.RemoveWhenDone && (result.Succeeded || spec.Detach))
        {
            Containers[spec.Name] = new ContainerInfo(
                spec.Name,
                spec.Image,
                spec.Detach,
                new Dictionary<string, string>(spec.Labels) { [ContainerLabels.Managed] = "true" },
                [.. spec.Ports.Select(p => $"{p.HostAddress}:{p.HostPort}")]);
        }

        return Task.FromResult(result);
    }

    public Task<ContainerResult> ExecAsync(string container, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        Calls.Add($"exec {container}");
        Require(container);
        return Task.FromResult(ExecHandler(container, command));
    }

    /// <summary>Every streaming exec: container, command, direction.</summary>
    public List<(string Container, IReadOnlyList<string> Command, StreamDirection Direction)> StreamExecs { get; } = [];

    /// <summary>Answers a streaming exec. The default is a process that ends at once with no output.</summary>
    public Func<string, IReadOnlyList<string>, StreamDirection, CancellationToken, IStreamingProcess>? StreamHandler { get; set; }

    public Task<IStreamingProcess> ExecStreamAsync(string container, IReadOnlyList<string> command, StreamDirection direction, CancellationToken cancellationToken)
    {
        Calls.Add($"exec-stream {container}");
        Require(container);
        StreamExecs.Add((container, command, direction));
        cancellationToken.ThrowIfCancellationRequested();
        var process = StreamHandler is null
            ? new FakeStreamingProcess(Stream.Null, cancellationToken: cancellationToken)
            : StreamHandler(container, command, direction, cancellationToken);
        return Task.FromResult(process);
    }

    /// <summary>Every file copied into a container: container, host path, container path, owner.</summary>
    public List<(string Container, string HostPath, string ContainerPath, string Owner)> Copies { get; } = [];

    /// <summary>Runs after a file is copied in: host path, container path.</summary>
    public Action<string, string>? OnCopy { get; set; }

    /// <summary>When set, <c>cp</c> throws it, as when the daemon refuses.</summary>
    public Exception? CopyFailure { get; set; }

    public Task CopyToAsync(string container, string hostPath, string containerPath, string owner, CancellationToken cancellationToken)
    {
        Calls.Add($"cp {container}");
        Require(container);
        if (CopyFailure is not null)
        {
            throw CopyFailure;
        }

        Copies.Add((container, hostPath, containerPath, owner));
        OnCopy?.Invoke(hostPath, containerPath);
        return Task.CompletedTask;
    }

    public Action<string>? OnInspect { get; set; }

    public Task<ContainerInfo?> InspectAsync(string name, CancellationToken cancellationToken)
    {
        OnInspect?.Invoke(name);
        return Task.FromResult(Containers.GetValueOrDefault(name));
    }

    public Task StartAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add($"start {name}");
        Containers[name] = Require(name) with { Running = true };
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add($"stop {name}");
        Containers[name] = Require(name) with { Running = false };
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add($"rm {name}");
        if (!Containers.TryGetValue(name, out var info))
        {
            return Task.FromResult(false);
        }

        Guard(info);
        if (FailRemove.Contains(name))
        {
            throw new SqlBranchException($"docker rm failed for '{name}': the daemon is busy.");
        }

        Containers.Remove(name);
        return Task.FromResult(true);
    }

    public Task<string> LogsAsync(string name, int tail, CancellationToken cancellationToken)
    {
        Require(name);
        return Task.FromResult(Log);
    }

    public Task<IReadOnlyList<ContainerInfo>> ListManagedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContainerInfo>>([.. Containers.Values.Where(c => c.IsManaged)]);

    public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) =>
        Task.FromResult(Images.Count == 0 || Images.Contains(image));

    public Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Volumes.Contains(name));

    public Task<bool> VolumeIsManagedAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Volumes.Contains(name) && !UnlabeledVolumes.Contains(name));

    public Task CreateVolumeAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add($"volume create {name}");
        Volumes.Add(name);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveVolumeAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add($"volume rm {name}");
        if (FailRemoveVolume.Contains(name))
        {
            throw new SqlBranchException($"docker volume rm failed for '{name}': in use.");
        }

        if (Volumes.Contains(name) && UnlabeledVolumes.Contains(name))
        {
            throw new SqlBranchException(ErrorCodes.ForeignContainer, $"The volume '{name}' was not created by SqlBranch, so SqlBranch will not remove it.");
        }

        return Task.FromResult(Volumes.Remove(name));
    }

    private ContainerInfo Require(string name)
    {
        var info = Containers.GetValueOrDefault(name) ?? throw new SqlBranchException($"The container '{name}' does not exist.");
        Guard(info);
        return info;
    }

    private static void Guard(ContainerInfo info)
    {
        if (!info.IsManaged)
        {
            throw new SqlBranchException(ErrorCodes.ForeignContainer, $"The container '{info.Name}' was not created by SqlBranch.");
        }
    }
}

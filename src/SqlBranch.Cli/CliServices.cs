using System.Collections;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;

namespace SqlBranch.Cli;

/// <summary>
/// What commands reach the outside world through: the container runtime, the
/// engine connection, the environment and the folders. Tests give fakes, so a
/// unit test never runs the real <c>docker</c>.
/// </summary>
internal sealed record CliServices(
    IContainerRuntime Runtime,
    IEngineConnector Connector,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    string? HomeDirectory,
    ManagedPaths Paths,
    EngineTimings Timings,
    string EngineName = ContainerLabels.EngineName,
    Func<string, bool>? DirectoryExists = null,
    Func<int, bool>? PortIsFree = null,
    string PoolVolume = ContainerLabels.PoolVolume,
    Func<string, ClientFile>? FileProbe = null,
    TimeProvider? Clock = null,
    Func<string, string>? RealPath = null,
    Func<BackupUrl, StorageSecrets, SecretRedactor, IArchiveStore>? StoreFactory = null,
    IProcessRunner? SqlPackageProcess = null,
    IBacpacReadSource? BacpacReadSource = null)
{
    /// <summary>The real thing: <c>docker</c>, SqlClient and the process's own environment.</summary>
    public static CliServices Real(SecretRedactor redactor)
    {
        var sources = SettingsSources.FromProcess();
        var environment = new Dictionary<string, string>(sources.Environment, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && SqlPackageTool.IsProbeEnvironmentVariable(key))
            {
                environment[key] = value;
            }
        }

        return new CliServices(
            new DockerRuntime(new SystemProcessRunner(), redactor),
            new SqlClientEngineConnector(),
            environment,
            sources.WorkingDirectory,
            sources.HomeDirectory,
            ManagedPaths.Default,
            EngineTimings.Default);
    }

    /// <summary>Whether a host directory exists. A missing bind source would be created by the runtime, so it is checked first.</summary>
    public Func<string, bool> DirectoryExistsCheck => DirectoryExists ?? Directory.Exists;

    /// <summary>A full path with every symbolic link in it resolved, so two spellings of one place compare equal (<c>/tmp</c> and <c>/private/tmp</c>).</summary>
    public Func<string, string> RealPathOf => RealPath ?? RealPaths.Resolve;

    /// <summary>Builds the storage client of an <c>https://</c> or <c>s3://</c> export destination or import source.</summary>
    public Func<BackupUrl, StorageSecrets, SecretRedactor, IArchiveStore> StoreOf =>
        StoreFactory ?? ((url, secrets, redactor) => ArchiveStores.Create(url, secrets, new StorageOptions(), redactor));

    /// <summary>Reads private BACPAC objects through the same authenticated storage clients as archives.</summary>
    public IBacpacReadSource BacpacReadSourceOf(SecretRedactor redactor) =>
        BacpacReadSource ?? ArchiveStores.CreateReadSource(StoreOf, redactor);

    /// <summary>What the client can say about a file, such as a backup the user names.</summary>
    public Func<string, ClientFile> ProbeFile => FileProbe ?? ClientFile.Probe;

    /// <summary>Whether nothing listens on 127.0.0.1 at the port.</summary>
    public Func<int, bool> PortIsFreeCheck => PortIsFree ?? PortProbe.IsFree;

    /// <summary>The pool directory used when <c>pool</c> is not set.</summary>
    public string DefaultPoolDirectory =>
        Path.Combine(HomeDirectory ?? WorkingDirectory, ".sqlbranch", "pool");

    /// <summary>Resolves the settings for one command from the flags, the environment and the files.</summary>
    public Settings LoadSettings(string? configFile) =>
        SettingsLoader.Load(new SettingsSources
        {
            Environment = Environment,
            WorkingDirectory = WorkingDirectory,
            ConfigFile = configFile,
            HomeDirectory = HomeDirectory,
        });

    /// <summary>The <c>sa</c> password the user gave, or null.</summary>
    public string? GivenPassword =>
        Environment.TryGetValue(SettingKeys.SaPasswordVariable, out var value) && value.Length > 0 ? value : null;
}

/// <summary>Tests whether a loopback port can be bound.</summary>
internal static class PortProbe
{
    public static bool IsFree(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}

/// <summary>A file on the client: whether it exists, whether it can be read, and how big it is.</summary>
internal sealed record ClientFile(bool Exists, bool Readable, long Length)
{
    /// <summary>Looks at the real file. A file counts as readable when it can be opened for reading.</summary>
    public static ClientFile Probe(string path)
    {
        if (!File.Exists(path))
        {
            return new ClientFile(false, false, 0);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new ClientFile(true, true, stream.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClientFile(true, false, 0);
        }
    }
}

/// <summary>Resolves symbolic links in a path.</summary>
internal static class RealPaths
{
    private const int MaxLinks = 40;

    /// <summary>
    /// The path with every existing symbolic link resolved, component by component. A part of the path that does not exist is kept as
    /// written, so the real path of a file that is not there yet is its real folder plus its name.
    /// </summary>
    public static string Resolve(string path) => Resolve(Path.GetFullPath(path), new int[1]);

    private static string Resolve(string full, int[] followed)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
            FileSystemInfo? target = null;
            try
            {
                target = info?.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: false);
            }
            catch (IOException)
            {
                // A link that cannot be read is kept as written.
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++followed[0] > MaxLinks)
            {
                return full;
            }

            current = Resolve(target.FullName, followed);
        }

        return current;
    }
}

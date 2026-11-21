using SqlBranch.Cli;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// A whole CLI over fakes: the runtime, the engine and the container host. A
/// unit test built on this can never reach the real <c>docker</c>.
/// </summary>
internal sealed class CliWorld
{
    public const string EngineImage = "mcr.microsoft.com/mssql/server:2022-latest";
    public const string StoredPassword = "Stored#Pass1";

    public CliWorld()
    {
        Runtime = new FakeContainerRuntime();
        Connector = new FakeEngineConnector();
        Host = new FakeInitHost(Runtime, Connector);
        Environment = new Dictionary<string, string> { ["SQLBRANCH_POOL"] = FakeInitHost.PoolDir,
            ["PATH"] = Path.GetTempPath() };
    }

    public FakeContainerRuntime Runtime { get; }

    public FakeEngineConnector Connector { get; }

    public FakeInitHost Host { get; }

    public Dictionary<string, string> Environment { get; }

    /// <summary>Whether the engine port is free on the client.</summary>
    public bool PortFree { get; set; } = true;

    /// <summary>The host directories that exist on the client.</summary>
    public HashSet<string> Directories { get; } = [];

    public SecretRedactor Redactor { get; } = new();

    /// <summary>The S3 and Azure storage a URL source or destination reaches; no test ever reaches a real service.</summary>
    public FakeStorageService S3 { get; } = new(StorageKind.S3);

    /// <summary>The Azure Blob Storage service of the world.</summary>
    public FakeStorageService Azure { get; } = new(StorageKind.Azure);

    /// <summary>The pool volume's name; tests that use a volume of their own set it, as an integration test world does.</summary>
    public string PoolVolume { get; set; } = ContainerLabels.PoolVolume;

    /// <summary>The files on the client that a command may name, by full path, and their sizes.</summary>
    public Dictionary<string, long> Files { get; } = [];

    /// <summary>Files on the client that exist but cannot be read.</summary>
    public HashSet<string> UnreadableFiles { get; } = [];

    /// <summary>Symbolic links on the client: a folder, and the folder it really is. A path under a key is resolved to the same path under its value.</summary>
    public Dictionary<string, string> Links { get; } = [];

    public FakeProcessRunner SqlPackageProcess { get; } = new()
    {
        Handler = _ => new ProcessOutput(0, "170.5.96.0", ""),
    };

    public TimeProvider? Clock { get; set; }

    public CliServices Services() => new(
        Runtime, Connector, Environment, "/work", "/home/u", ManagedPaths.Default,
        new EngineTimings { ReadyTimeout = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(5) },
        ContainerLabels.EngineName,
        path => Directories.Contains(path),
        _ => PortFree,
        PoolVolume,
        path => new ClientFile(Files.ContainsKey(path), Files.ContainsKey(path) && !UnreadableFiles.Contains(path), Files.GetValueOrDefault(path)), Clock,
        RealPath: path => Links.Aggregate(path, (p, link) => p == link.Key || p.StartsWith(link.Key + "/", StringComparison.Ordinal) ? link.Value + p[link.Key.Length..] : p),
        StoreFactory: (url, secrets, redactor) => ArchiveStores.Create(url, secrets,
            new StorageOptions { Handler = url.Kind == SqlBranch.Core.Images.BackupUrlKind.S3 ? S3 : Azure, RetryDelay = TimeSpan.Zero }, redactor), SqlPackageProcess: SqlPackageProcess);

    public (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.CreateDefault(Redactor, Services()).Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>An engine that an earlier init left, on the plain pool <see cref="FakeInitHost.PoolDir"/>.</summary>
    public void WithEngine(bool running = true, string backend = "plain", string? pool = null)
    {
        Host.AddExistingEngine(EngineImage, backend, pool ?? FakeInitHost.PoolDir, running: running);
        Connector.SaPassword = StoredPassword;
    }
}

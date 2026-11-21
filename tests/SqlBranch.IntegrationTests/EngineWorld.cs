using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SqlBranch.Cli;
using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// One test engine: a name, a port, a managed pool folder, a pool volume name and a
/// password of its own, never <c>sqlbranch-engine</c>, 14330, <c>/var/lib/sqlbranch/pool.xfs</c>
/// or the <c>sqlbranch-pool</c> volume. It also gets a home folder that does not exist, so no
/// default under the user's home can be reached. Everything it created is removed on dispose,
/// including the empty folder in the container host that the pool file lived in.
/// </summary>
public sealed class EngineWorld : IAsyncDisposable
{
    /// <summary>The real SQL Server image; the engine needs it, unlike the work containers.</summary>
    public const string EngineImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>Set to <c>plain</c> to run the suite on a pool without reflink: every world then uses a plain folder of the client.</summary>
    public const string PoolBackendVariable = "SQLBRANCH_IT_POOL_BACKEND";

    private readonly HostPoolDirectory? _plainDirectory;

    /// <param name="poolSize">The size of the managed pool file; unused for a plain pool.</param>
    /// <param name="plain">
    /// <c>null</c> follows <see cref="PoolBackendVariable"/>; <c>false</c> always uses the managed loop-xfs pool, for a test that is about it.
    /// </param>
    public EngineWorld(string poolSize = "2GB", bool? plain = null)
    {
        Id = DockerHost.NewId();
        EngineName = $"sqlbranch-it-engine-{Id}";
        PoolVolume = $"sqlbranch-it-pool-{Id}";
        HomeDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sqlbranch-it-home-{Id}");
        Paths = DockerHost.TestPaths(Id);
        Port = FreePort();
        Password = "Sb#" + Guid.NewGuid().ToString("N");
        Redactor = new SecretRedactor();
        Runtime = new DockerRuntime(new SystemProcessRunner(), Redactor);
        Environment = new Dictionary<string, string>
        {
            ["SQLBRANCH_POOL_BACKEND"] = "loop-xfs",
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "",
            ["SQLBRANCH_POOL_SIZE"] = poolSize,
            ["SQLBRANCH_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SQLBRANCH_SA_PASSWORD"] = Password,
            ["SQLBRANCH_ENGINE_IMAGE"] = EngineImage,
        };
        IsPlain = plain ?? PlainRequested;
        if (IsPlain)
        {
            _plainDirectory = new HostPoolDirectory(Runtime);
            Environment["SQLBRANCH_POOL_BACKEND"] = "plain";
            Environment["SQLBRANCH_POOL"] = _plainDirectory.Path;
        }
    }

    /// <summary>True when <see cref="PoolBackendVariable"/> is <c>plain</c>.</summary>
    public static bool PlainRequested =>
        string.Equals(System.Environment.GetEnvironmentVariable(PoolBackendVariable), "plain", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this world's pool is a plain folder of the client, not the managed XFS file.</summary>
    public bool IsPlain { get; }

    /// <summary>Where this world's pool is, for a test that looks inside it.</summary>
    public PoolLocation Location => _plainDirectory?.Location ?? Paths.Location;

    public string Id { get; }

    public string EngineName { get; }

    /// <summary>The copy-mode pool volume of this world, never the product's <c>sqlbranch-pool</c>.</summary>
    public string PoolVolume { get; }

    /// <summary>A folder that is never created: the default pool directory and the user config file live under it.</summary>
    public string HomeDirectory { get; }

    public ManagedPaths Paths { get; }

    public int Port { get; }

    public string Password { get; }

    public SecretRedactor Redactor { get; }

    public DockerRuntime Runtime { get; }

    public Dictionary<string, string> Environment { get; }

    public EngineEndpoint Endpoint => EngineEndpoint.Loopback(Port);

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Runs one command line the way the tool does, with this world's settings.</summary>
    public (int Exit, string Out, string Err, TimeSpan Elapsed) RunCli(params string[] args) => RunCliAt(null, args);

    public (int Exit, string Out, string Err, TimeSpan Elapsed) RunCliAt(TimeProvider? time, params string[] args)
    {
        var services = new CliServices(Runtime, new SqlClientEngineConnector(), Environment, System.Environment.CurrentDirectory,
            HomeDirectory, Paths, EngineTimings.Default, EngineName, PoolVolume: PoolVolume, Clock: time);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var clock = Stopwatch.StartNew();
        var exit = CliApp.CreateDefault(Redactor, services).Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString(), clock.Elapsed);
    }

    /// <summary>
    /// Like <see cref="RunCli"/>, with the storage clients of <c>https://</c> and <c>s3://</c> destinations and sources built on the
    /// handler <paramref name="handlerFor"/> returns (design D9: the client cannot reach the fixture's bridge address, and trusts only
    /// the fixture's test authority). Every <c>docker</c> command line is added to <paramref name="commandLines"/> when given.
    /// </summary>
    public (int Exit, string Out, string Err, TimeSpan Elapsed) RunCliWithStorage(
        Func<SqlBranch.Core.Images.BackupUrl, HttpMessageHandler> handlerFor, List<string>? commandLines, int? partSize,
        Action<IReadOnlyList<string>>? beforeDockerStart, params string[] args)
    {
        IContainerRuntime runtime = commandLines is null && beforeDockerStart is null
            ? Runtime
            : new DockerRuntime(new RecordingProcessRunner(new SystemProcessRunner(), commandLines ?? []) { BeforeStart = beforeDockerStart }, Redactor);
        var services = new CliServices(runtime, new SqlClientEngineConnector(), Environment, System.Environment.CurrentDirectory,
            HomeDirectory, Paths, EngineTimings.Default, EngineName, PoolVolume: PoolVolume,
            StoreFactory: (url, secrets, redactor) => SqlBranch.Core.Storage.ArchiveStores.Create(url, secrets,
                new SqlBranch.Core.Storage.StorageOptions { Handler = handlerFor(url), PartSize = partSize }, redactor));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var clock = Stopwatch.StartNew();
        var exit = CliApp.CreateDefault(Redactor, services).Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString(), clock.Elapsed);
    }

    /// <summary>
    /// Starts the tool as a separate process against this world, so a test can run two at once or kill one.
    /// It is the same command line as <see cref="RunCli"/>, built by the harness project, which takes the engine,
    /// volume and pool folder of this world instead of the product's defaults.
    /// </summary>
    public CliProcess StartCli(params string[] args) => Start(interruptible: false, args);

    internal CliProcess StartCli(IReadOnlyDictionary<string, string> overrides, params string[] args) =>
        Start(interruptible: false, args, overrides);

    /// <summary>
    /// Like <see cref="StartCli"/>, for a test that sends Ctrl+C (SIGINT). A process started from a shell script or a test runner in the
    /// background inherits an ignored SIGINT, which .NET then keeps ignoring, so the program is started through <c>perl</c>, which
    /// resets the signal and replaces itself with the program (the process id stays the same).
    /// </summary>
    public CliProcess StartCliInterruptible(params string[] args) => Start(interruptible: true, args);

    internal CliProcess StartCliInterruptible(IReadOnlyDictionary<string, string> overrides, params string[] args) =>
        Start(interruptible: true, args, overrides);

    private CliProcess Start(bool interruptible, string[] args, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var baseDirectory = AppContext.BaseDirectory.Replace("SqlBranch.IntegrationTests", "SqlBranch.CliHarness", StringComparison.Ordinal);
        var dll = System.IO.Path.Combine(baseDirectory, "SqlBranch.CliHarness.dll");
        Assert.True(File.Exists(dll), $"The harness is not built: {dll}");
        var info = new ProcessStartInfo(interruptible ? "perl" : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (interruptible)
        {
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("$SIG{INT} = 'DEFAULT'; exec @ARGV or die \"exec failed: $!\"");
            info.ArgumentList.Add("dotnet");
        }

        info.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in Environment)
        {
            info.Environment[key] = value;
        }

        info.Environment["SQLBRANCH_HARNESS_ENGINE"] = EngineName;
        info.Environment["SQLBRANCH_HARNESS_VOLUME"] = PoolVolume;
        info.Environment["SQLBRANCH_HARNESS_POOL_ROOT"] = Paths.Root;
        info.Environment["SQLBRANCH_HARNESS_HOME"] = HomeDirectory;
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                info.Environment[key] = value;
            }
        }

        return new CliProcess(Process.Start(info)!);
    }

    /// <summary>The pool the lookup of the tool finds for this world, for tests that build the objects of the tool directly.</summary>
    public async Task<FoundPool> FindPoolAsync()
    {
        var services = new CliServices(Runtime, new SqlClientEngineConnector(), Environment, System.Environment.CurrentDirectory,
            HomeDirectory, Paths, EngineTimings.Default, EngineName, PoolVolume: PoolVolume);
        var finder = new PoolFinder(Runtime, Paths, null, EngineName, PoolVolume);
        return await finder.RequireAsync(services.LoadSettings(null), services.DefaultPoolDirectory, CancellationToken.None);
    }

    /// <summary>Runs docker on this world's own container or a read-only query about it.</summary>
    public static async Task<ProcessOutput> DockerAsync(params string[] args) =>
        await new SystemProcessRunner().RunAsync("docker", args, null, CancellationToken.None);

    /// <summary>Waits until the engine takes a login with this password, for at most the product's 120 seconds.</summary>
    public async Task<bool> CanLoginAsync(string password)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var session = await new SqlClientEngineConnector().ConnectAsync(Endpoint, password, cts.Token);
            return Convert.ToInt32(await session.ScalarAsync("SELECT 1", cts.Token), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
#pragma warning disable CA1031 // Any failure to log in means the login does not work.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Only things this world created: its own engine, its own pool file and its own folder.
        try
        {
            await Runtime.RemoveAsync(EngineName, CancellationToken.None);
        }
        catch (SqlBranchException)
        {
            // Not ours to remove: leave it.
        }

        try
        {
            // Only a volume with this world's own name, and only if it carries SqlBranch's label.
            await Runtime.RemoveVolumeAsync(PoolVolume, CancellationToken.None);
        }
        catch (SqlBranchException)
        {
            // Not labeled, so not ours: leave it.
        }

        var pool = new ManagedPool(Runtime, Paths);
        try
        {
            await pool.UnmountAsync(CancellationToken.None);
            await pool.DeleteFileAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                await DockerHost.RemoveTestFolderAsync(Runtime, Paths, Id);
            }
            finally
            {
                if (_plainDirectory is not null)
                {
                    await _plainDirectory.DisposeAsync();
                }
            }
        }
    }
}

/// <summary>A tool process started by <see cref="EngineWorld.StartCli"/>.</summary>
public sealed class CliProcess : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _out;
    private readonly Task<string> _err;
    private bool _disposed;

    public CliProcess(Process process)
    {
        _process = process;
        _process.StandardInput.Close();
        _out = _process.StandardOutput.ReadToEndAsync();
        _err = _process.StandardError.ReadToEndAsync();
    }

    public bool HasExited => _process.HasExited;

    /// <summary>Sends SIGINT, which the tool takes as Ctrl+C.</summary>
    public void Interrupt()
    {
        using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-INT", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false });
        kill!.WaitForExit();
    }

    /// <summary>Ends the process at once, as a crash or a closed terminal would.</summary>
    public void Kill() => _process.Kill(entireProcessTree: true);

    public async Task<(int Exit, string Out, string Err)> WaitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _process.WaitForExitAsync(cts.Token);
        return (_process.ExitCode, await _out, await _err);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }
}

/// <summary>Passes every call to the real runner and keeps the command line, so a test can search what was run.</summary>
public sealed class RecordingProcessRunner(IProcessRunner inner, List<string> lines) : IProcessRunner
{
    /// <summary>Runs before a streaming command starts, with its arguments: a test uses it to change the pool at a chosen moment.</summary>
    public Action<IReadOnlyList<string>>? BeforeStart { get; init; }

    public Task<ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, string? standardInput = null)
    {
        Record(fileName, arguments, environment);
        return inner.RunAsync(fileName, arguments, environment, cancellationToken, standardInput);
    }

    public IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, StreamDirection direction, CancellationToken cancellationToken)
    {
        Record(fileName, arguments, environment);
        BeforeStart?.Invoke(arguments);
        return inner.Start(fileName, arguments, environment, direction, cancellationToken);
    }

    private void Record(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        lock (lines)
        {
            lines.Add($"{fileName} {string.Join(' ', arguments)} {(environment is null ? string.Empty : string.Join(' ', environment.Select(e => $"{e.Key}={e.Value}")))}");
        }
    }
}

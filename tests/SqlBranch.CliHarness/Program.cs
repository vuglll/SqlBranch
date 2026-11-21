using System.Globalization;
using SqlBranch.Cli;
using SqlBranch.Core.Configuration;
using SqlBranch.Testing;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Images;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Storage;
using SqlBranch.IntegrationTests;

// The sqlbranch command line, pointed at the engine, pool volume and pool folder of one integration test world.
// The real tool has no setting for these names, and a test must never reach the product's own defaults, so the
// test passes them in these variables. Everything else (settings, password) comes from SQLBRANCH_* as usual.
// Only integration tests start this program, to have a client process they can kill.
var engine = Required("SQLBRANCH_HARNESS_ENGINE");
var volume = Required("SQLBRANCH_HARNESS_VOLUME");
var root = Required("SQLBRANCH_HARNESS_POOL_ROOT");
var home = Required("SQLBRANCH_HARNESS_HOME");

var redactor = new SecretRedactor();
var services = CliServices.Real(redactor) with
{
    EngineName = engine,
    PoolVolume = volume,
    Paths = new ManagedPaths(root),
    HomeDirectory = home,
};
// A test that wants the docker command lines of this process names a file; each line is appended as the command starts.
if (Environment.GetEnvironmentVariable("SQLBRANCH_HARNESS_COMMAND_LOG") is { Length: > 0 } commandLog)
{
    services = services with { Runtime = new DockerRuntime(new LoggingRunner(new SystemProcessRunner(), commandLog), redactor) };
}

if (Environment.GetEnvironmentVariable("SQLBRANCH_HARNESS_PUBLISH_READY") is { Length: > 0 } publishReady)
{
    services = services with { Runtime = new DockerRuntime(new PausedPublicationRunner(publishReady), redactor) };
}

if (args is ["fixture-create", var image])
{
    var sources = new SettingsSources { WorkingDirectory = services.WorkingDirectory, HomeDirectory = home, Environment = services.Environment };
    var host = new FixtureHost(services.Runtime, services.Connector, redactor, sources, services.Paths, engine, volume);
    await using var db = await SqlBranchDatabase.CreateAsync(image, new(), host, SqlBranchEnvironment.Process,
        () => Guid.NewGuid().ToString("N")[..8]);
    Console.WriteLine(db.Name);
    return 0;
}
if (Environment.GetEnvironmentVariable("SQLBRANCH_HARNESS_DOWNLOAD_READY") is { Length: > 0 } ready)
{
    using var handler = FixtureStorageTransport.CreateHandler(
        Required("SQLBRANCH_HARNESS_STORAGE_CERT"),
        int.Parse(Required("SQLBRANCH_HARNESS_STORAGE_PORT"), CultureInfo.InvariantCulture));
    var source = ArchiveStores.CreateReadSource(
        (url, secrets, scrubber) => ArchiveStores.Create(url, secrets, new StorageOptions { Handler = handler }, scrubber), redactor);
    services = services with { BacpacReadSource = new PausedDownloadSource(source, ready) };
    return CliApp.CreateDefault(redactor, services).Run(args, Console.Out, Console.Error);
}

return CliApp.CreateDefault(redactor, services).Run(args, Console.Out, Console.Error);

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is not set. This program is for the integration tests only.");

sealed class PausedDownloadSource(IBacpacReadSource inner, string ready) : IBacpacReadSource
{
    public ValueTask<long> HeadAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken) =>
        inner.HeadAsync(url, secrets, cancellationToken);

    public async ValueTask<Stream> GetAsync(BackupUrl url, StorageSecrets secrets, CancellationToken cancellationToken) =>
        new PausedBody(await inner.GetAsync(url, secrets, cancellationToken).ConfigureAwait(false), ready);

    private sealed class PausedBody(Stream body, string ready) : Stream
    {
        private bool _sentFirstChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirstChunk)
            {
                _sentFirstChunk = true;
                // Exceed the scratch FileStream buffer so the next read starts after a visible partial write.
                var count = Math.Min(buffer.Length, 16 * 1024);
                return await body.ReadAtLeastAsync(buffer[..count], count, false, cancellationToken).ConfigureAwait(false);
            }

            File.WriteAllText(ready, string.Empty);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The download was expected to be interrupted.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

sealed class PausedPublicationRunner(string ready) : IProcessRunner
{
    private readonly SystemProcessRunner _inner = new();

    public async Task<ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken, string? standardInput = null)
    {
        if (arguments.Any(arg => arg.Contains($"mv -nT -- '{ImagePaths.BuildRoot}/l", StringComparison.Ordinal)))
        {
            File.WriteAllText(ready, string.Empty);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        return await _inner.RunAsync(fileName, arguments, environment, cancellationToken, standardInput).ConfigureAwait(false);
    }

    public IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment,
        StreamDirection direction, CancellationToken cancellationToken) =>
        _inner.Start(fileName, arguments, environment, direction, cancellationToken);
}

sealed class LoggingRunner(IProcessRunner inner, string path) : IProcessRunner
{
    private static readonly object Gate = new();

    public Task<ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, string? standardInput = null)
    {
        Log(fileName, arguments, environment);
        return inner.RunAsync(fileName, arguments, environment, cancellationToken, standardInput);
    }

    public IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, StreamDirection direction, CancellationToken cancellationToken)
    {
        Log(fileName, arguments, environment);
        return inner.Start(fileName, arguments, environment, direction, cancellationToken);
    }

    private void Log(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        lock (Gate)
        {
            File.AppendAllText(path, $"{fileName} {string.Join(' ', arguments)} {(environment is null ? string.Empty : string.Join(' ', environment.Select(e => $"{e.Key}={e.Value}")))}\n");
        }
    }
}

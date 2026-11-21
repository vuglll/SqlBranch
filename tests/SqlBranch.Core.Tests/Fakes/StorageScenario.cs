using System.Formats.Tar;
using System.Text;
using SqlBranch.Cli;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Images;
using SqlBranch.Core.Storage;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>Every test that builds a real storage SDK client runs in this collection, one at a time, because some set process-wide variables.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StorageSdkSerial
{
    public const string Name = "storage-sdk";
}

/// <summary>
/// A CLI over fakes with an image <c>shop</c> to export (the engine's tar is scripted) and the means to import an archive that sits in
/// a fake storage service, so a whole export or import to a URL runs through the real command and the real SDK clients.
/// </summary>
internal sealed class StorageScenario
{
    public const string KeyId = "AKIDSQLBRANCHTEST";
    public const string Secret = "s3cr3tTestValue9Xy";
    public const string Sas = "sv=2024-11-04&sp=rcw&sig=AbC123secretSig";
    public const string S3Url = "s3://s3.amazonaws.com/images/nightly/shop.sbimg";
    public const string S3Key = "images/nightly/shop.sbimg";
    public const string BlobUrl = "https://acme.blob.core.windows.net/images/shop.sbimg";
    public const string BlobKey = "images/shop.sbimg";

    private readonly List<string> _timeline = [];

    public StorageScenario(int dataBytes = 300_000, int logBytes = 70_000)
    {
        World = new CliWorld();
        Data = new byte[dataBytes];
        Log = new byte[logBytes];
        new Random(11).NextBytes(Data);
        new Random(12).NextBytes(Log);
        Manifest = new ImageManifest(
            ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4135.4",
            [new ImageFile("Shop", "shop.mdf", "data", Data.Length), new ImageFile("Shop_log", "shop_log.ldf", "log", Log.Length)],
            "FULL", "SIMPLE", 268_435_456, LogRebuilt: true, new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero),
            "shop.bak", new string('a', 64), new Dictionary<string, string>(), "mcr.microsoft.com/mssql/server:2022-latest");

        World.WithEngine();
        World.Directories.Add("/work");
        var inner = World.Runtime.ExecHandler;
        World.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script == ImageCatalog.ListingScript)
            {
                return FakeContainerRuntime.Ok($"SQLBRANCH_IMAGE|shop|{Convert.ToBase64String(Encoding.UTF8.GetBytes(Manifest.ToJson()))}");
            }

            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_FREE=" + (100L << 30));
            }

            if (script.Contains("SQLBRANCH_EXISTS=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_EXISTS=no");
            }

            if (script.Contains("stat -c %s", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"{Data.Length}\n{Log.Length}");
            }

            if (script.Contains("SQLBRANCH_TAR=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_TAR=ended");
            }

            return script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal) ? inner(container, command) : FakeContainerRuntime.Ok();
        };
        World.Runtime.StreamHandler = (_, command, direction, token) =>
        {
            _timeline.Add(direction == StreamDirection.Read ? "tar-read" : "tar-write");
            return direction == StreamDirection.Read
                ? new FakeStreamingProcess(new Pipe(TarBytes()), cancellationToken: token)
                : new FakeStreamingProcess(new CountingSink(), cancellationToken: token);
        };
    }

    public CliWorld World { get; }

    public byte[] Data { get; }

    public byte[] Log { get; }

    public ImageManifest Manifest { get; }

    /// <summary>The kinds of stream exec that ran, in order: <c>tar-read</c> for an export, <c>tar-write</c> for an import.</summary>
    public IReadOnlyList<string> Timeline => _timeline;

    public void UseS3Secrets()
    {
        World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = KeyId;
        World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = Secret;
        World.S3.ExpectedAccessKeyId = KeyId;
        World.S3.EchoSecret = Secret;
    }

    public void UseSas(bool leadingQuestionMark = true)
    {
        World.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = (leadingQuestionMark ? "?" : string.Empty) + Sas;
        World.Azure.ExpectedSasToken = Sas;
        World.Azure.EchoSecret = Sas;
    }

    /// <summary>An archive of the image <c>shop</c>, as an export writes it.</summary>
    public Task<byte[]> ArchiveAsync() => ArchiveCrafter.ValidAsync(Manifest, Data, Log);

    public (int Exit, string Out, string Err) Export(params string[] args)
    {
        World.Runtime.Calls.Clear();
        return World.Run(["image", "export", "shop", .. args]);
    }

    public (int Exit, string Out, string Err) Import(params string[] args)
    {
        World.Runtime.Calls.Clear();
        return World.Run(["image", "import", .. args]);
    }

    /// <summary>What <c>tar -C data -cf - shop.mdf shop_log.ldf</c> prints, in GNU format and padded to whole records.</summary>
    private byte[] TarBytes()
    {
        using var output = new MemoryStream();
        using (var tar = new TarWriter(output, TarEntryFormat.Gnu, leaveOpen: true))
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "shop.mdf") { DataStream = new MemoryStream(Data) });
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "shop_log.ldf") { DataStream = new MemoryStream(Log) });
        }

        var padded = new byte[(output.Length + 10239) / 10240 * 10240];
        output.ToArray().CopyTo(padded, 0);
        return padded;
    }

    /// <summary>A stream that reads forward only, like a pipe.</summary>
    private sealed class Pipe(byte[] bytes) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(Math.Min(buffer.Length, 16384), bytes.Length - _position);
            bytes.AsSpan((int)_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

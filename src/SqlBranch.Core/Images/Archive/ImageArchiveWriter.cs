using System.Formats.Tar;
using System.Security.Cryptography;
using SqlBranch.Core.Output;
using ZstdSharp;

namespace SqlBranch.Core.Images.Archive;

/// <summary>
/// Writes an image archive (design D2) to a stream: the manifest first, then each data file as it arrives, then the checksums.
/// Data is hashed while it passes, so no file is read twice and none is held in memory.
/// </summary>
public sealed class ImageArchiveWriter : IAsyncDisposable
{
    private readonly CompressionStream _compressed;
    private readonly TarWriter _tar;
    private readonly List<ArchiveFileChecksum> _checksums = [];
    private ImageManifest? _manifest;
    private string? _manifestSha256;
    private bool _finished;

    /// <param name="output">Where the archive goes.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open when the writer is disposed.</param>
    public ImageArchiveWriter(Stream output, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        _compressed = new CompressionStream(output, level: 1, leaveOpen: leaveOpen);
        _tar = new TarWriter(_compressed, TarEntryFormat.Pax, leaveOpen: true);
    }

    /// <summary>Called with the number of data bytes written so far, after each block of a file.</summary>
    public Action<long>? OnData { get; init; }

    /// <summary>Data bytes written so far.</summary>
    public long DataBytes { get; private set; }

    /// <summary>Writes the first entry: the archive format and the manifest exactly as stored.</summary>
    public async Task WriteHeadAsync(ImageManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_manifest is not null)
        {
            throw new InvalidOperationException("The head was already written.");
        }

        var bytes = ArchiveHeadJson.Write(manifest);
        _manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await WriteEntryAsync(ImageArchive.HeadEntry, new MemoryStream(bytes), manifest.BuiltAt, cancellationToken).ConfigureAwait(false);
        _manifest = manifest;
    }

    /// <summary>
    /// Writes the next data file. Files must come in manifest order and <paramref name="content"/> must hold exactly the manifest's
    /// size, or this throws <see cref="InvalidDataException"/> (nothing more is written then; the archive is not usable).
    /// </summary>
    public async Task<ArchiveFileChecksum> WriteFileAsync(ImageFile file, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);
        var manifest = _manifest ?? throw new InvalidOperationException("The head must be written first.");
        if (_checksums.Count >= manifest.Files.Count || manifest.Files[_checksums.Count].FileName != file.FileName)
        {
            throw new InvalidOperationException($"'{file.FileName}' is not the next file of the manifest.");
        }

        using var hashing = new HashingReadStream(content, file.SizeBytes, bytes =>
        {
            DataBytes += bytes;
            OnData?.Invoke(DataBytes);
        });
        try
        {
            await WriteEntryAsync(ImageArchive.DataEntry(file.FileName), hashing, manifest.BuiltAt, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            // TarWriter wanted to seek the read-once stream (a future runtime change); report it as an unusable archive, not a raw exception.
            throw new InvalidDataException($"{file.FileName} could not be written to the archive: {ex.Message}", ex);
        }

        if (hashing.Count != file.SizeBytes)
        {
            throw new InvalidDataException(
                $"{file.FileName} ended after {hashing.Count} of {file.SizeBytes} bytes.");
        }

        var checksum = new ArchiveFileChecksum(file.FileName, file.SizeBytes, hashing.Hash());
        _checksums.Add(checksum);
        return checksum;
    }

    /// <summary>Writes the checksums and finishes the tar and zstd streams. Every file must have been written.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        var manifest = _manifest ?? throw new InvalidOperationException("The head must be written first.");
        if (_checksums.Count != manifest.Files.Count)
        {
            throw new InvalidOperationException("Not every file of the manifest was written.");
        }

        var json = System.Text.Encoding.UTF8.GetBytes(new ArchiveChecksums(_manifestSha256!, _checksums).ToJson());
        await WriteEntryAsync(ImageArchive.ChecksumsEntry, new MemoryStream(json), manifest.BuiltAt, cancellationToken).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            await _tar.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _compressed.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteEntryAsync(string name, Stream data, DateTimeOffset time, CancellationToken cancellationToken)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            ModificationTime = time,
        };
        await _tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Reads exactly <c>length</c> bytes from a stream for a tar entry, hashing and counting them, and refuses to read past <c>length</c>.
/// It says it can seek, because <see cref="TarWriter"/> refuses an unseekable data stream for an unseekable archive stream,
/// but it only accepts a seek to where it already is, so it is read once from start to end.
/// </summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly Action<long>? _onRead;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public HashingReadStream(Stream inner, long length, Action<long>? onRead = null)
    {
        _inner = inner;
        _length = length;
        _onRead = onRead;
    }

    public long Count { get; private set; }

    public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => Count;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var limited = buffer[..(int)Math.Min(buffer.Length, _length - Count)];
        var read = _inner.Read(limited);
        Account(limited[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var limited = buffer[..(int)Math.Min(buffer.Length, _length - Count)];
        var read = await _inner.ReadAsync(limited, cancellationToken).ConfigureAwait(false);
        Account(limited.Span[..read]);
        return read;
    }

    private void Account(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        _hash.AppendData(data);
        Count += data.Length;
        _onRead?.Invoke(data.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Count + offset,
            _ => _length + offset,
        };
        return target == Count ? Count : throw new NotSupportedException("The stream is read once, from start to end.");
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

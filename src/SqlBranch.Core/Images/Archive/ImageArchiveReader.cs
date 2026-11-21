using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using SqlBranch.Core.Output;
using ZstdSharp;

namespace SqlBranch.Core.Images.Archive;

/// <summary>One entry of an archive's tar, as the reader found it. Nothing about it has been trusted yet.</summary>
/// <param name="Name">The entry's name as stored.</param>
/// <param name="Type">The tar entry type.</param>
/// <param name="Length">The size in the entry's header.</param>
/// <param name="Data">The entry's bytes, readable once and only until the next entry is asked for; null when it has none.</param>
public sealed record ArchiveEntry(string Name, TarEntryType Type, long Length, Stream? Data);

/// <summary>
/// Reads an image archive (design D2). <see cref="OpenAsync"/> finds the compression by the stream's first bytes and reads and checks
/// the first entry; nothing is written anywhere. The rest of the entries are handed on one at a time for the caller to check.
/// </summary>
public sealed class ImageArchiveReader : IAsyncDisposable
{
    private readonly TarReader _tar;
    private readonly Stream _decompressed;

    private ImageArchiveReader(TarReader tar, Stream decompressed, ArchiveHead head)
    {
        _tar = tar;
        _decompressed = decompressed;
        Head = head;
    }

    /// <summary>The first entry: the archive format and the manifest.</summary>
    public ArchiveHead Head { get; }

    /// <summary>
    /// Detects the compression, reads the first entry and checks the archive format. Throws exit 1 <c>archive_invalid</c> when
    /// <paramref name="input"/> is not an image archive this version reads, or <c>archive_corrupt</c> when zstd decoding fails.
    /// </summary>
    public static async Task<ImageArchiveReader> OpenAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var magic = new byte[4];
        var got = await input.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        var seen = magic.AsMemory(0, got);
        var replay = new PrefixedStream(seen.ToArray(), input);
        Stream decompressed;
        if (seen.Span.StartsWith(ImageArchive.ZstdMagic))
        {
            decompressed = new ZstdArchiveStream(replay);
        }
        else if (seen.Span.StartsWith(ImageArchive.GzipMagic))
        {
            decompressed = new GZipStream(replay, CompressionMode.Decompress, leaveOpen: true);
        }
        else
        {
            throw ArchiveHeadJson.Invalid("its compression is unrecognized. Expected zstd or gzip.");
        }

        var tar = new TarReader(decompressed, leaveOpen: true);
        try
        {
            var first = await NextRawAsync(tar, cancellationToken).ConfigureAwait(false);
            if (first is not { Name: ImageArchive.HeadEntry, Type: TarEntryType.RegularFile or TarEntryType.V7RegularFile, Data: not null })
            {
                throw ArchiveHeadJson.Invalid($"its first entry is not {ImageArchive.HeadEntry}.");
            }

            if (first.Length > ImageArchive.MaxMetadataBytes)
            {
                throw ArchiveHeadJson.Invalid($"its {ImageArchive.HeadEntry} is too large.");
            }

            var bytes = new byte[first.Length];
            await first.Data.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var head = ArchiveHeadJson.Read(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return new ImageArchiveReader(tar, decompressed, head);
        }
        catch (Exception ex)
        {
            await tar.DisposeAsync().ConfigureAwait(false);
            await decompressed.DisposeAsync().ConfigureAwait(false);
            if (ex is ZstdException or ZstdFrameEndException)
            {
                throw Corrupt();
            }

            if (ex is InvalidDataException or IOException)
            {
                throw ArchiveHeadJson.Invalid("its first entry cannot be read.");
            }

            throw;
        }
    }

    /// <summary>
    /// The next entry, or null at the end of the archive. Throws exit 1 <c>archive_corrupt</c> when the compressed stream or the tar
    /// is damaged or ends early.
    /// </summary>
    public async Task<ArchiveEntry?> NextAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await NextRawAsync(_tar, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ZstdException)
        {
            throw Corrupt();
        }
    }

    internal async Task ValidateEndAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _decompressed.CopyToAsync(Stream.Null, bufferSize: 64 * 1024, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ZstdException)
        {
            throw Corrupt();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _decompressed.DisposeAsync().ConfigureAwait(false);
        await _tar.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<ArchiveEntry?> NextRawAsync(TarReader tar, CancellationToken cancellationToken)
    {
        var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : new ArchiveEntry(entry.Name, entry.EntryType, entry.Length, entry.DataStream);
    }

    private static SqlBranchException Corrupt() =>
        new(ErrorCodes.ArchiveCorrupt, "The archive is damaged or ends early.");

    // Distinguish the decoder's premature EOF from a short tar header or entry inside a complete frame.
    private sealed class ZstdArchiveStream(Stream input) : DecompressionStream(input, checkEndOfStream: true, leaveOpen: true)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new ZstdFrameEndException(ex);
            }
        }
    }

    private sealed class ZstdFrameEndException(EndOfStreamException inner) : EndOfStreamException(inner.Message, inner);
}

/// <summary>A read-only stream that first returns bytes already taken from another stream, then continues with it.</summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _rest;
    private int _used;

    public PrefixedStream(byte[] prefix, Stream rest)
    {
        _prefix = prefix;
        _rest = rest;
    }

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
        if (_used < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _used);
            _prefix.AsSpan(_used, count).CopyTo(buffer);
            _used += count;
            return count;
        }

        return _rest.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_used < _prefix.Length)
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }

        return _rest.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

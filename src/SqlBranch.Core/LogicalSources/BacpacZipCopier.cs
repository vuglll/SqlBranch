using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SqlBranch.Core.LogicalSources;

/// <summary>Relocates ZIP records without decoding their compressed data.</summary>
internal sealed class BacpacZipCopier
{
    private const uint LocalSignature = 0x04034b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint DescriptorSignature = 0x08074b50;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const int LocalSize = 30;
    private const int CentralSize = 46;
    private const int EndSize = 22;
    private const int Zip64EndSize = 56;
    private const int Zip64LocatorSize = 20;
    private const int DescriptorFlag = 1 << 3;
    private const int Utf8Flag = 1 << 11;
    private const int EncryptionFlags = (1 << 0) | (1 << 6) | (1 << 13);
    private const ushort Zip64Version = 45;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private readonly Stream _source;
    private readonly List<Entry> _entries = [];
    private readonly Entry[] _physicalEntries;
    private readonly byte[] _comment;

    public BacpacZipCopier(Stream source, CancellationToken token)
    {
        _source = source;
        try
        {
            token.ThrowIfCancellationRequested();
            var (offset, end, count, comment) = ReadDirectoryLocation(source);
            _comment = comment;
            var directoryOffset = offset;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0L; index < count; ++index)
            {
                token.ThrowIfCancellationRequested();
                var record = ReadHeader(source, offset, end, CentralSize, CentralSignature, nameLengthAt: 28);
                var entry = ReadCentralEntry(record);
                if (!names.Add(entry.Name))
                {
                    throw BacpacPackage.Invalid();
                }
                _entries.Add(entry);
                offset += record.Length;
            }
            if (offset != end)
            {
                throw BacpacPackage.Invalid();
            }

            // Physical order can differ from directory order. Bound each local record by the next one.
            _physicalEntries = _entries.OrderBy(entry => entry.Offset).ToArray();
            for (var index = 0; index < _physicalEntries.Length; ++index)
            {
                token.ThrowIfCancellationRequested();
                var limit = index + 1 < _physicalEntries.Length
                    ? Math.Min(directoryOffset, _physicalEntries[index + 1].Offset) : directoryOffset;
                ReadLocalEntry(source, _physicalEntries[index], limit);
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException)
        {
            throw BacpacPackage.Invalid();
        }
    }

    public async Task WriteAsync(Stream output, Stream spool, Replacement model, Replacement origin, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            foreach (var entry in _physicalEntries)
            {
                token.ThrowIfCancellationRequested();
                var localOffset = offset;
                var replacement = entry.Name.Equals("model.xml", StringComparison.OrdinalIgnoreCase) ? model
                    : entry.Name.Equals("Origin.xml", StringComparison.OrdinalIgnoreCase) ? origin : null;
                if (replacement is null)
                {
                    _source.Position = entry.Offset;
                    await CopyAsync(_source, output, entry.LocalLength, buffer, token).ConfigureAwait(false);
                    offset += entry.LocalLength;
                    entry.Record = Relocate(entry, localOffset);
                }
                else
                {
                    var local = ReadAt(_source, entry.Offset, entry.LocalHeaderLength);
                    local = SetReplacement(local, replacement, central: false, offset: 0);
                    await output.WriteAsync(local, token).ConfigureAwait(false);
                    spool.Position = replacement.Offset;
                    await CopyAsync(spool, output, replacement.Compressed, buffer, token).ConfigureAwait(false);
                    offset += local.Length + replacement.Compressed;
                    entry.Record = SetReplacement(entry.Record, replacement, central: true, localOffset);
                }
            }

            var directoryOffset = offset;
            foreach (var entry in _entries)
            {
                token.ThrowIfCancellationRequested();
                await output.WriteAsync(entry.Record, token).ConfigureAwait(false);
                offset += entry.Record.Length;
            }
            await output.WriteAsync(WriteEnd(directoryOffset, offset, _entries.Count, _comment), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyAsync(Stream source, Stream output, long length, byte[] buffer, CancellationToken token)
    {
        while (length > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw BacpacPackage.Invalid();
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            length -= read;
        }
    }

    public static async Task<Replacement> CompressAsync(Stream spool, Func<Stream, Task> write)
    {
        var offset = spool.Position;
        var deflate = new DeflateStream(spool, CompressionLevel.Optimal, leaveOpen: true);
        using var content = new CrcStream(deflate);
        await using (deflate.ConfigureAwait(false))
        {
            await write(content).ConfigureAwait(false);
        }
        return new Replacement(offset, spool.Position - offset, content.Length, content.Checksum);
    }

    private static (long Offset, long End, long Count, byte[] Comment) ReadDirectoryLocation(Stream source)
    {
        var tailLength = (int)Math.Min(source.Length, EndSize + ushort.MaxValue);
        var tail = ReadAt(source, source.Length - tailLength, tailLength);
        var end = tail.Length - EndSize;
        while (end >= 0 && (U32(tail, end) != EndSignature || U16(tail, end + 20) != tail.Length - end - EndSize))
        {
            --end;
        }
        if (end < 0)
        {
            throw BacpacPackage.Invalid();
        }

        var trailer = source.Length - tail.Length + end;
        long count = U16(tail, end + 10);
        long size = U32(tail, end + 12);
        long offset = U32(tail, end + 16);
        var disk = U16(tail, end + 4);
        var directoryDisk = U16(tail, end + 6);
        long diskCount = U16(tail, end + 8);
        if (trailer >= Zip64LocatorSize && (size != trailer - offset ||
            count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue ||
            disk == ushort.MaxValue || directoryDisk == ushort.MaxValue))
        {
            var locatorOffset = trailer - Zip64LocatorSize;
            var locator = ReadAt(source, locatorOffset, Zip64LocatorSize);
            if (U32(locator, 0) == Zip64LocatorSignature)
            {
                var zip64Offset = I64(locator, 8);
                if (U32(locator, 4) != 0 || U32(locator, 16) != 1 || zip64Offset > locatorOffset - Zip64EndSize)
                {
                    throw BacpacPackage.Invalid();
                }
                var zip64 = ReadAt(source, zip64Offset, Zip64EndSize);
                var zip64Size = I64(zip64, 4);
                var zip64Count = I64(zip64, 32);
                var zip64DirectorySize = I64(zip64, 40);
                var zip64DirectoryOffset = I64(zip64, 48);
                if (U32(zip64, 0) != Zip64EndSignature || zip64Size != locatorOffset - zip64Offset - 12 ||
                    U32(zip64, 16) != 0 || U32(zip64, 20) != 0 || I64(zip64, 24) != zip64Count ||
                    !Matches(disk, 0, ushort.MaxValue) || !Matches(directoryDisk, 0, ushort.MaxValue) ||
                    !Matches(count, zip64Count, ushort.MaxValue) || !Matches(diskCount, zip64Count, ushort.MaxValue) ||
                    !Matches(size, zip64DirectorySize, uint.MaxValue) || !Matches(offset, zip64DirectoryOffset, uint.MaxValue))
                {
                    throw BacpacPackage.Invalid();
                }
                disk = directoryDisk = 0;
                (count, diskCount, size, offset, trailer) = (zip64Count, zip64Count, zip64DirectorySize, zip64DirectoryOffset, zip64Offset);
            }
        }

        if (disk != 0 || directoryDisk != 0 || diskCount != count || offset > trailer || size != trailer - offset || count > size / CentralSize)
        {
            throw BacpacPackage.Invalid();
        }
        return (offset, trailer, count, tail[(end + EndSize)..]);
    }

    private static bool Matches(long classic, long actual, long sentinel) => classic == sentinel || classic == actual;

    private static Entry ReadCentralEntry(byte[] record)
    {
        var flags = U16(record, 8);
        if ((flags & EncryptionFlags) != 0)
        {
            throw BacpacPackage.Invalid();
        }
        var name = record.AsSpan(CentralSize, U16(record, 28));
        long compressed = U32(record, 20);
        long size = U32(record, 24);
        long offset = U32(record, 42);
        long disk = U16(record, 34);
        var extraLength = U16(record, 30);
        var zip64 = FindZip64(record, CentralSize + name.Length, extraLength);
        var hasZip64 = zip64.Data != 0;
        var position = 0;
        var zip64Sizes = false;
        // Without a ZIP64 extra field, the maximum classic value is literal.
        if (hasZip64 && size == uint.MaxValue)
        {
            size = ReadZip64(record, zip64, ref position);
            zip64Sizes = size != uint.MaxValue;
        }
        if (hasZip64 && compressed == uint.MaxValue)
        {
            compressed = ReadZip64(record, zip64, ref position);
            zip64Sizes |= compressed != uint.MaxValue;
        }
        var zip64Offset = -1;
        if (hasZip64 && offset == uint.MaxValue)
        {
            zip64Offset = zip64.Data + position;
            offset = ReadZip64(record, zip64, ref position);
        }
        if (disk == ushort.MaxValue)
        {
            if (zip64.Length - position < 4)
            {
                throw BacpacPackage.Invalid();
            }
            disk = U32(record, zip64.Data + position);
        }
        if (disk != 0)
        {
            throw BacpacPackage.Invalid();
        }
        // ZipArchive defaults to UTF-8 with replacement fallback when bit 11 is clear.
        var encoding = Encoding.UTF8;
        if ((flags & Utf8Flag) != 0)
        {
            encoding = StrictUtf8;
            _ = encoding.GetCharCount(record.AsSpan(CentralSize + name.Length + extraLength));
        }
        return new Entry(record, encoding.GetString(name), offset, compressed, size, zip64Offset, zip64Sizes);
    }

    private static void ReadLocalEntry(Stream source, Entry entry, long limit)
    {
        var local = ReadHeader(source, entry.Offset, limit, LocalSize, LocalSignature, nameLengthAt: 26);
        var central = entry.Record;
        var flags = U16(local, 6);
        var crc = U32(central, 16);
        if (flags != U16(central, 8) || U16(local, 8) != U16(central, 10) ||
            !local.AsSpan(LocalSize, U16(local, 26)).SequenceEqual(central.AsSpan(CentralSize, U16(central, 28))))
        {
            throw BacpacPackage.Invalid();
        }

        var usesDescriptor = (flags & DescriptorFlag) != 0;
        long compressed = U32(local, 18);
        long size = U32(local, 22);
        var zip64 = FindZip64(local, LocalSize + U16(local, 26), U16(local, 28));
        var localZip64 = zip64.Data != 0 && (size == uint.MaxValue || compressed == uint.MaxValue);
        if (localZip64)
        {
            var position = 0;
            var zip64Size = ReadZip64(local, zip64, ref position);
            var zip64Compressed = ReadZip64(local, zip64, ref position);
            if (size == uint.MaxValue)
            {
                size = zip64Size;
            }
            if (compressed == uint.MaxValue)
            {
                compressed = zip64Compressed;
            }
        }
        var dataOffset = entry.Offset + local.Length;
        if (!Matches(compressed, entry.Compressed, usesDescriptor ? 0 : -1) ||
            !Matches(size, entry.Size, usesDescriptor ? 0 : -1) ||
            !Matches(U32(local, 14), crc, usesDescriptor ? 0 : -1) ||
            entry.Compressed > limit - dataOffset)
        {
            throw BacpacPackage.Invalid();
        }
        entry.LocalHeaderLength = local.Length;
        entry.LocalLength = local.Length + entry.Compressed;
        if (usesDescriptor)
        {
            var position = dataOffset + entry.Compressed;
            var descriptor = ReadAt(source, position, (int)Math.Min(24, limit - position));
            // Relocating a classic record may add an extended offset and repeat literal
            // maximum sizes in the central extra field without changing its descriptor.
            var zip64Descriptor = localZip64 || entry.Zip64Sizes || entry.Compressed > uint.MaxValue || entry.Size > uint.MaxValue;
            entry.LocalLength += DescriptorLength(descriptor, entry, crc, zip64Descriptor, limit - position);
        }
    }

    private static int DescriptorLength(byte[] bytes, Entry entry, uint crc, bool zip64, long available)
    {
        // A descriptor may omit its signature. Check the CRC even when it equals the signature.
        Span<int> lengths = stackalloc int[4];
        var count = 0;
        for (var prefix = 0; prefix <= 4; prefix += 4)
        {
            if (prefix != 0 && (bytes.Length < prefix || U32(bytes, 0) != DescriptorSignature))
            {
                continue;
            }
            var start = prefix + 4;
            if (bytes.Length < start || U32(bytes, prefix) != crc)
            {
                continue;
            }
            if (!zip64 && bytes.Length >= start + 8 &&
                U32(bytes, start) == entry.Compressed && U32(bytes, start + 4) == entry.Size)
            {
                lengths[count++] = start + 8;
            }
            if (bytes.Length >= start + 16 &&
                U64(bytes, start) == (ulong)entry.Compressed && U64(bytes, start + 8) == (ulong)entry.Size)
            {
                lengths[count++] = start + 16;
            }
        }
        if (count == 1)
        {
            return lengths[0];
        }
        if (count > 1 && available <= 24 && lengths[..count].Contains((int)available))
        {
            return (int)available;
        }
        throw BacpacPackage.Invalid();
    }

    private static byte[] Relocate(Entry entry, long offset)
    {
        var record = entry.Record;
        if (entry.Zip64Offset >= 0)
        {
            Put64(record, entry.Zip64Offset, offset);
        }
        else if (offset < uint.MaxValue)
        {
            Put32(record, 42, (uint)offset);
        }
        else
        {
            var extraStart = CentralSize + U16(record, 28);
            var extraLength = U16(record, 30);
            var zip64 = FindZip64(record, extraStart, extraLength);
            // In a central directory's ZIP64 field the offset follows any extended sizes.
            var sizes = (U32(record, 24) == uint.MaxValue ? 8 : 0) + (U32(record, 20) == uint.MaxValue ? 8 : 0);
            var payload = new byte[Math.Max(zip64.Length, sizes) + 8];
            if (zip64.Data != 0)
            {
                var original = record.AsSpan(zip64.Data, zip64.Length);
                original[..sizes].CopyTo(payload);
                original[sizes..].CopyTo(payload.AsSpan(sizes + 8));
            }
            else
            {
                // Any literal maximum sizes need explicit slots once a ZIP64 offset is added.
                for (var position = 0; position < sizes; position += 8)
                {
                    Put64(payload, position, uint.MaxValue);
                }
            }
            Put64(payload, sizes, offset);
            record = WriteZip64(record, 30, zip64.Data == 0 ? extraStart : zip64.Data - 4,
                zip64.Data == 0 ? 0 : zip64.Length + 4, payload);
            Put32(record, 42, uint.MaxValue);
            Put16(record, 6, Math.Max(U16(record, 6), Zip64Version));
        }
        return record;
    }

    private static byte[] SetReplacement(byte[] record, Replacement replacement, bool central, long offset)
    {
        var extraLengthAt = central ? 30 : 28;
        var extraStart = (central ? CentralSize : LocalSize) + U16(record, extraLengthAt - 2);
        var needsZip64 = replacement.Size > uint.MaxValue || replacement.Compressed > uint.MaxValue || offset > uint.MaxValue;
        var largeSize = needsZip64 && replacement.Size >= uint.MaxValue;
        var largeCompressed = needsZip64 && replacement.Compressed >= uint.MaxValue;
        var largeOffset = needsZip64 && offset >= uint.MaxValue;
        // A local ZIP64 field contains both sizes. The central field supplies every sentinel when present.
        var localZip64 = !central && (largeSize || largeCompressed);
        Span<byte> payload = stackalloc byte[24];
        var length = 0;
        if (localZip64 || largeSize)
        {
            Put64(payload, length, replacement.Size);
            length += 8;
        }
        if (localZip64 || largeCompressed)
        {
            Put64(payload, length, replacement.Compressed);
            length += 8;
        }
        if (largeOffset)
        {
            Put64(payload, length, offset);
            length += 8;
        }
        record = WriteZip64(record, extraLengthAt, extraStart, U16(record, extraLengthAt), payload[..length]);

        var versionAt = central ? 6 : 4;
        var flagsAt = versionAt + 2;
        var crcAt = central ? 16 : 14;
        Put16(record, versionAt, Math.Max(U16(record, versionAt), length > 0 ? Zip64Version : (ushort)20));
        Put16(record, flagsAt, (ushort)(U16(record, flagsAt) & ~(DescriptorFlag | 6)));
        Put16(record, flagsAt + 2, 8); // Deflate
        Put32(record, crcAt, replacement.Crc);
        Put32(record, crcAt + 4, localZip64 || largeCompressed ? uint.MaxValue : (uint)replacement.Compressed);
        Put32(record, crcAt + 8, localZip64 || largeSize ? uint.MaxValue : (uint)replacement.Size);
        if (central)
        {
            Put16(record, 34, 0);
            Put32(record, 42, largeOffset ? uint.MaxValue : (uint)offset);
        }
        return record;
    }

    private static byte[] WriteZip64(byte[] record, int extraLengthAt, int start, int oldLength, ReadOnlySpan<byte> payload)
    {
        var newLength = payload.IsEmpty ? 0 : payload.Length + 4;
        var extraLength = U16(record, extraLengthAt) - oldLength + newLength;
        if (extraLength > ushort.MaxValue)
        {
            throw BacpacPackage.Invalid();
        }
        if (oldLength == 0 && newLength == 0)
        {
            return record;
        }
        var updated = new byte[record.Length - oldLength + newLength];
        record.AsSpan(0, start).CopyTo(updated);
        if (newLength != 0)
        {
            Put16(updated, start, 1);
            Put16(updated, start + 2, (ushort)payload.Length);
            payload.CopyTo(updated.AsSpan(start + 4));
        }
        record.AsSpan(start + oldLength).CopyTo(updated.AsSpan(start + newLength));
        Put16(updated, extraLengthAt, (ushort)extraLength);
        return updated;
    }

    private static byte[] WriteEnd(long offset, long end, int count, byte[] comment)
    {
        var size = end - offset;
        var zip64 = offset >= uint.MaxValue || size >= uint.MaxValue || count >= ushort.MaxValue;
        var endAt = zip64 ? Zip64EndSize + Zip64LocatorSize : 0;
        var result = new byte[endAt + EndSize + comment.Length];
        if (zip64)
        {
            Put32(result, 0, Zip64EndSignature);
            Put64(result, 4, Zip64EndSize - 12);
            Put16(result, 12, Zip64Version);
            Put16(result, 14, Zip64Version);
            Put64(result, 24, count);
            Put64(result, 32, count);
            Put64(result, 40, size);
            Put64(result, 48, offset);
            Put32(result, Zip64EndSize, Zip64LocatorSignature);
            Put64(result, Zip64EndSize + 8, end);
            Put32(result, Zip64EndSize + 16, 1);
        }
        Put32(result, endAt, EndSignature);
        Put16(result, endAt + 8, (ushort)Math.Min(count, ushort.MaxValue));
        Put16(result, endAt + 10, (ushort)Math.Min(count, ushort.MaxValue));
        Put32(result, endAt + 12, (uint)Math.Min(size, uint.MaxValue));
        Put32(result, endAt + 16, (uint)Math.Min(offset, uint.MaxValue));
        Put16(result, endAt + 20, (ushort)comment.Length);
        comment.CopyTo(result, endAt + EndSize);
        return result;
    }

    private static byte[] ReadHeader(Stream source, long offset, long limit, int fixedSize, uint signature, int nameLengthAt)
    {
        if (offset > limit - fixedSize)
        {
            throw BacpacPackage.Invalid();
        }
        var header = ReadAt(source, offset, fixedSize);
        if (U32(header, 0) != signature)
        {
            throw BacpacPackage.Invalid();
        }
        var length = fixedSize + U16(header, nameLengthAt) + U16(header, nameLengthAt + 2);
        if (fixedSize == CentralSize)
        {
            length += U16(header, nameLengthAt + 4);
        }
        if (length > limit - offset)
        {
            throw BacpacPackage.Invalid();
        }
        Array.Resize(ref header, length);
        source.ReadExactly(header.AsSpan(fixedSize));
        return header;
    }

    private static Zip64Field FindZip64(byte[] record, int start, int length)
    {
        var result = default(Zip64Field);
        var end = start + length;
        while (start < end)
        {
            if (end - start < 4)
            {
                throw BacpacPackage.Invalid();
            }
            var size = U16(record, start + 2);
            if (size > end - start - 4)
            {
                throw BacpacPackage.Invalid();
            }
            if (U16(record, start) == 1)
            {
                if (result.Data != 0)
                {
                    throw BacpacPackage.Invalid();
                }
                result = new Zip64Field(start + 4, size);
            }
            start += 4 + size;
        }
        return result;
    }

    private static long ReadZip64(byte[] bytes, Zip64Field field, ref int position)
    {
        if (field.Length - position < 8)
        {
            throw BacpacPackage.Invalid();
        }
        var value = I64(bytes, field.Data + position);
        position += 8;
        return value;
    }

    private static byte[] ReadAt(Stream source, long position, int length)
    {
        source.Position = position;
        var bytes = new byte[length];
        source.ReadExactly(bytes);
        return bytes;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset)
    {
        var value = U64(bytes, offset);
        return value <= long.MaxValue ? (long)value : throw BacpacPackage.Invalid();
    }
    private static void Put16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    private static void Put32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void Put64(Span<byte> bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes[offset..], value);

    private readonly record struct Zip64Field(int Data, int Length);

    internal sealed record Replacement(long Offset, long Compressed, long Size, uint Crc);

    private sealed record Entry(byte[] Original, string Name, long Offset, long Compressed, long Size, int Zip64Offset, bool Zip64Sizes)
    {
        public byte[] Record { get; set; } = Original;
        public int LocalHeaderLength { get; set; }
        public long LocalLength { get; set; }
    }

    private sealed class CrcStream(Stream output) : Stream
    {
        private long _length;
        private uint _crc = uint.MaxValue;

        public uint Checksum => ~_crc;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc = BacpacCrc32.Update(_crc, buffer);
            _length += buffer.Length;
            output.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _crc = BacpacCrc32.Update(_crc, buffer.Span);
            _length += buffer.Length;
            return output.WriteAsync(buffer, cancellationToken);
        }
    }
}

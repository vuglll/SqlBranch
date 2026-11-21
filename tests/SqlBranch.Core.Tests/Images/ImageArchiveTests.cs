using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Output;
using SqlBranch.Core.Tests.Fakes;
using ZstdSharp;

namespace SqlBranch.Core.Tests.Images;

/// <summary>The archive writer and reader of design D2.</summary>
public sealed class ImageArchiveTests
{
    private static ImageManifest Manifest(params ImageFile[] files) => new(
        ImageManifest.CurrentFormat, "shop", "Shop", "16.0.4135.4", files, "FULL", "SIMPLE", 268_435_456, LogRebuilt: true,
        new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero), "shop.bak", new string('a', 64), new Dictionary<string, string>(),
        "mcr.microsoft.com/mssql/server:2022-latest");

    private static ImageManifest Small(out byte[] data, out byte[] log)
    {
        data = new byte[300_000];
        log = new byte[70_001];
        new Random(7).NextBytes(data);
        new Random(8).NextBytes(log);
        return Manifest(new ImageFile("Shop", "shop.mdf", "data", data.Length), new ImageFile("Shop_log", "shop_log.ldf", "log", log.Length));
    }

    private static async Task<byte[]> WriteAsync(ImageManifest manifest, params byte[][] contents)
    {
        using var output = new MemoryStream();
        await using (var writer = new ImageArchiveWriter(output))
        {
            await writer.WriteHeadAsync(manifest, CancellationToken.None);
            for (var i = 0; i < contents.Length; i++)
            {
                await writer.WriteFileAsync(manifest.Files[i], new MemoryStream(contents[i]), CancellationToken.None);
            }

            await writer.FinishAsync(CancellationToken.None);
        }

        return output.ToArray();
    }

    /// <summary>A gzip stream of a tar with the given entries, for archives a real export would never write.</summary>
    private static byte[] Craft(params (string Name, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            }
        }

        return output.ToArray();
    }

    private static async Task<List<(string Name, byte[] Data)>> ReadAllAsync(ImageArchiveReader reader)
    {
        var entries = new List<(string, byte[])>();
        while (await reader.NextAsync(CancellationToken.None) is { } entry)
        {
            using var copy = new MemoryStream();
            if (entry.Data is not null)
            {
                await entry.Data.CopyToAsync(copy);
            }

            entries.Add((entry.Name, copy.ToArray()));
        }

        return entries;
    }

    [Fact]
    public async Task A_zstd_archive_round_trips_its_manifest_and_files()
    {
        var manifest = Small(out var data, out var log);

        var archive = await WriteAsync(manifest, data, log);
        await using var reader = await ImageArchiveReader.OpenAsync(new MemoryStream(archive), CancellationToken.None);
        var entries = await ReadAllAsync(reader);

        Assert.Equal(ImageArchive.CurrentFormat, reader.Head.ArchiveFormat);
        Assert.Equal(manifest.ToJson(), reader.Head.Manifest.ToJson());
        Assert.Equal(["data/shop.mdf", "data/shop_log.ldf", "sqlbranch-checksums.json"], entries.Select(e => e.Name));
        Assert.Equal(data, entries[0].Data);
        Assert.Equal(log, entries[1].Data);

        var checksums = ArchiveChecksums.Parse(entries[2].Data);
        Assert.Equal(reader.Head.Sha256, checksums.ManifestSha256);
        Assert.Equal(
            [new ArchiveFileChecksum("shop.mdf", data.Length, Convert.ToHexStringLower(SHA256.HashData(data))),
             new ArchiveFileChecksum("shop_log.ldf", log.Length, Convert.ToHexStringLower(SHA256.HashData(log)))],
            checksums.Files);
    }

    [Fact]
    public async Task The_entries_come_in_the_order_of_the_design_with_the_head_first_and_the_checksums_last()
    {
        var manifest = Small(out var data, out var log);
        var archive = await WriteAsync(manifest, data, log);

        // Read the raw tar, not through the reader, so the head is seen as an entry too.
        using var compressed = new MemoryStream(archive);
        using var decompressed = new DecompressionStream(compressed);
        using var tar = new TarReader(decompressed);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } entry)
        {
            Assert.Equal(TarEntryFormat.Pax, entry.Format);
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            names.Add(entry.Name);
        }

        Assert.Equal(["sqlbranch-archive.json", "data/shop.mdf", "data/shop_log.ldf", "sqlbranch-checksums.json"], names);
    }

    [Fact]
    public async Task An_archive_starts_with_the_zstd_magic_bytes()
    {
        var manifest = Small(out var data, out var log);

        var archive = await WriteAsync(manifest, data, log);

        Assert.Equal([0x28, 0xb5, 0x2f, 0xfd], archive[..4]);
    }

    [Fact]
    public async Task A_nine_gibibyte_entry_streams_through_without_being_buffered()
    {
        const long size = 9L << 30;
        var manifest = Manifest(new ImageFile("Shop", "shop.mdf", "data", size));
        using var output = new MemoryStream();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        ArchiveFileChecksum written;
        await using (var writer = new ImageArchiveWriter(output))
        {
            await writer.WriteHeadAsync(manifest, CancellationToken.None);
            var source = new SyntheticStream(size, fill: 3);
            written = await writer.WriteFileAsync(manifest.Files[0], source, CancellationToken.None);
            await writer.FinishAsync(CancellationToken.None);
            Assert.True(source.LargestRead <= 1 << 20, $"A read asked for {source.LargestRead} bytes.");
        }

        Assert.Equal(size, written.SizeBytes);

        // Read it back the same way, hashing as the bytes pass.
        output.Position = 0;
        await using var reader = await ImageArchiveReader.OpenAsync(output, CancellationToken.None);
        var entry = await reader.NextAsync(CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal("data/shop.mdf", entry.Name);
        Assert.Equal(size, entry.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await entry.Data!.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            total += read;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(size, total);
        Assert.Equal(written.Sha256, Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.True(allocated < 1L << 30, $"Streaming 9 GiB twice allocated {allocated} bytes.");
    }

    [Fact]
    public async Task A_newer_archive_format_is_archive_invalid_and_says_a_newer_sqlbranch_wrote_it()
    {
        var archive = Craft(("sqlbranch-archive.json", "{\"archiveFormat\":2,\"manifest\":{\"somethingNew\":true}}"));

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => ImageArchiveReader.OpenAsync(new MemoryStream(archive), CancellationToken.None));

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("newer SqlBranch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x42, 0x5a, 0x68, 0x39, 1, 2, 3 })]
    [InlineData(new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2, 3 })]
    [InlineData(new byte[] { 0x28, 0xb5, 0x2f })]
    [InlineData(new byte[] { 0x1f })]
    [InlineData(new byte[0])]
    public async Task An_unknown_compression_is_archive_invalid(byte[] bytes)
    {
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => ImageArchiveReader.OpenAsync(new MemoryStream(bytes), CancellationToken.None));

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
        Assert.Contains("Expected zstd or gzip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_file_is_archive_invalid()
    {
        var text = Encoding.UTF8.GetBytes("These are my notes, not an archive.\n");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => ImageArchiveReader.OpenAsync(new MemoryStream(text), CancellationToken.None));

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
    }

    [Fact]
    public async Task A_gzip_file_that_is_not_a_tar_or_has_another_first_entry_is_archive_invalid()
    {
        using var plain = new MemoryStream();
        await using (var gzip = new GZipStream(plain, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("just compressed text, long enough to look like a header: " + new string('x', 700)));
        }

        var ex1 = await Assert.ThrowsAsync<SqlBranchException>(() => ImageArchiveReader.OpenAsync(new MemoryStream(plain.ToArray()), CancellationToken.None));
        var ex2 = await Assert.ThrowsAsync<SqlBranchException>(() =>
            ImageArchiveReader.OpenAsync(new MemoryStream(Craft(("data/shop.mdf", "x"))), CancellationToken.None));
        var ex3 = await Assert.ThrowsAsync<SqlBranchException>(() =>
            ImageArchiveReader.OpenAsync(new MemoryStream(Craft(("sqlbranch-archive.json", "not json"))), CancellationToken.None));

        Assert.All([ex1, ex2, ex3], ex => Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code));
    }

    [Fact]
    public async Task A_small_zstd_archive_cut_inside_the_head_is_archive_corrupt()
    {
        byte[] data = [1, 2, 3];
        byte[] log = [4, 5, 6];
        var manifest = Manifest(new ImageFile("Shop", "shop.mdf", "data", data.Length),
            new ImageFile("Shop_log", "shop_log.ldf", "log", log.Length));
        var archive = await WriteAsync(manifest, data, log);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            ImageArchiveReader.OpenAsync(new MemoryStream(archive[..(archive.Length / 2)]), CancellationToken.None));

        Assert.Equal(ErrorCodes.ArchiveCorrupt, ex.Code);
    }

    [Fact]
    public async Task A_complete_zstd_frame_with_an_incomplete_tar_header_is_archive_invalid()
    {
        using var compressor = new Compressor();
        var archive = compressor.Wrap(new byte[] { 1 }).ToArray();

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            ImageArchiveReader.OpenAsync(new MemoryStream(archive), CancellationToken.None));

        Assert.Equal(ErrorCodes.ArchiveInvalid, ex.Code);
    }

    [Fact]
    public async Task The_writer_refuses_files_out_of_order_and_content_of_the_wrong_size()
    {
        var manifest = Small(out var data, out var log);
        using var output = new MemoryStream();
        await using var writer = new ImageArchiveWriter(output);
        await writer.WriteHeadAsync(manifest, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteFileAsync(manifest.Files[1], new MemoryStream(log), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteFileAsync(manifest.Files[0], new MemoryStream(data[..^4096]), CancellationToken.None));
    }

    [Fact]
    public async Task A_content_stream_the_tar_writer_cannot_use_is_an_invalid_data_failure_not_a_raw_exception()
    {
        var manifest = Small(out _, out _);
        using var output = new MemoryStream();
        await using var writer = new ImageArchiveWriter(output);
        await writer.WriteHeadAsync(manifest, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteFileAsync(manifest.Files[0], new UnusableStream(), CancellationToken.None));
    }

    private sealed class UnusableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("no reading");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Free_space_in_a_file_makes_the_zstd_archive_small()
    {
        var manifest = Manifest(new ImageFile("Shop", "shop.mdf", "data", 8 << 20));
        var archive = await WriteAsync(manifest, new byte[8 << 20]);

        Assert.True(archive.Length < 100_000, $"8 MiB of zeros made an archive of {archive.Length} bytes.");
    }

    /// <summary>The engine's GNU tar writes a size over 8 GiB in base-256; the export reads it with <see cref="TarReader"/>.</summary>
    [Fact]
    public void A_gnu_tar_header_with_a_size_over_8_GiB_in_base_256_is_read_as_that_size()
    {
        const long size = 9L << 30;
        var header = new byte[512];
        Encoding.ASCII.GetBytes("shop.mdf").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000640\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("0001750\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0001750\0").CopyTo(header, 116);
        header[124] = 0x80;
        for (var i = 0; i < 8; i++)
        {
            header[135 - i] = (byte)(size >> (8 * i));
        }

        Encoding.ASCII.GetBytes("14000000000\0").CopyTo(header, 136);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar  \0").CopyTo(header, 257);
        Array.Fill(header, (byte)' ', 148, 8);
        var sum = header.Sum(b => (int)b);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);

        using var tar = new TarReader(new HeaderThenZeros(header, size), leaveOpen: false);
        var entry = tar.GetNextEntry(copyData: false);

        Assert.NotNull(entry);
        Assert.Equal("shop.mdf", entry.Name);
        Assert.Equal(size, entry.Length);
    }

    /// <summary>A tar of one entry whose bytes are zeros, readable only forwards, as the engine's pipe is.</summary>
    private sealed class HeaderThenZeros(byte[] header, long size) : Stream
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = header.Length + ((size + 511) / 512 * 512) + 1024;
            var n = (int)Math.Min(count, total - _position);
            for (var i = 0; i < n; i++)
            {
                buffer[offset + i] = _position + i < header.Length ? header[_position + i] : (byte)0;
            }

            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

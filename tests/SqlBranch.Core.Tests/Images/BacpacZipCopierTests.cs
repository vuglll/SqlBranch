using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using SqlBranch.Core.LogicalSources;
using SqlBranch.Core.Output;
using Xunit.Abstractions;

namespace SqlBranch.Core.Tests.Images;

public sealed class BacpacZipCopierTests(ITestOutputHelper output)
{
    private const string DataName = "Data/dbo.Items/TableData-000-00000.BCP";
    private const string ModelName = "MODEL.xml";
    private const string OriginName = "origin.XML";
    private const string ContentTypes = "[Content_Types].xml";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Timestamp = new(2025, 4, 3, 12, 34, 56, TimeSpan.Zero);
    private static readonly string Model = $"<DataSchemaModel xmlns='{BacpacPackage.ModelNamespace}'><Model>" +
        BacpacPackageTests.User("app", "LoginIdentifierScript") +
        BacpacPackageTests.User("reader", "LoginIdentifierScript") + "</Model></DataSchemaModel>";
    private static readonly string Origin = $"<DacOrigin xmlns='{BacpacPackage.ModelNamespace}'>" +
        "<PackageProperties><ContainsExportedData>true</ContainsExportedData></PackageProperties></DacOrigin>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preserves_raw_local_records_and_central_metadata(bool dataDescriptors)
    {
        using var scratch = NewScratch();
        var original = await StageAsync(scratch, CreateZip(dataDescriptors));
        Assert.True((await BacpacPackage.ValidateAsync(scratch.Package, None)).NeedsRewrite);

        var result = await BacpacPackage.RewriteAsync(scratch, None, stream => new NonSeekableOutput(stream));

        Assert.Equal(["app", "reader"], result.Users);
        Assert.False((await BacpacPackage.ValidateAsync(scratch.RewrittenPackage, None)).NeedsRewrite);
        Assert.Equal(original, await File.ReadAllBytesAsync(scratch.Package));
        var copy = await File.ReadAllBytesAsync(scratch.RewrittenPackage);
        AssertCopiedRecords(original, copy, dataDescriptors);
        using var before = ZipFile.OpenRead(scratch.Package);
        using var after = ZipFile.OpenRead(scratch.RewrittenPackage);
        Assert.Equal(before.Entries.Select(e => e.FullName), after.Entries.Select(e => e.FullName));
        foreach (var (entry, rewritten) in before.Entries.Zip(after.Entries))
        {
            Assert.Equal(entry.LastWriteTime, rewritten.LastWriteTime);
            Assert.Equal(entry.ExternalAttributes, rewritten.ExternalAttributes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handcrafted_zip64_round_trips_with_extended_sizes_offsets_and_optional_descriptor(bool descriptor)
    {
        using var scratch = NewScratch();
        var original = await StageAsync(scratch, CreateZip64(descriptor));
        Assert.True((await BacpacPackage.ValidateAsync(scratch.Package, None)).NeedsRewrite);

        await BacpacPackage.RewriteAsync(scratch, None);

        Assert.False((await BacpacPackage.ValidateAsync(scratch.RewrittenPackage, None)).NeedsRewrite);
        AssertCopiedRecords(original, await File.ReadAllBytesAsync(scratch.RewrittenPackage), descriptor, descriptorHasSignature: false);
        using var zip = ZipFile.OpenRead(scratch.RewrittenPackage);
        Assert.Equal([ContentTypes, DataName, OriginName, ModelName], zip.Entries.Select(e => e.FullName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Reads_maximum_classic_sizes_and_offset_or_real_zip64_values(bool zip64Sizes, bool zip64Offset)
    {
        var size = (long)uint.MaxValue + (zip64Sizes ? 1 : 0);
        var offset = (long)uint.MaxValue + (zip64Offset ? 1 : 0);
        var local = new byte[31 + (zip64Sizes ? 20 : 0)];
        Set32(local, 0, 0x04034b50);
        Set16(local, 4, zip64Sizes ? (ushort)45 : (ushort)20);
        Set16(local, 6, 8);
        Set32(local, 18, uint.MaxValue);
        Set32(local, 22, uint.MaxValue);
        Set16(local, 26, 1);
        local[30] = (byte)'a';

        var descriptor = new byte[zip64Sizes ? 24 : 16];
        Set32(descriptor, 0, 0x08074b50);
        var central = new byte[47 + (zip64Offset ? 28 : 0)];
        Set32(central, 0, 0x02014b50);
        Set16(central, 6, zip64Offset ? (ushort)45 : (ushort)20);
        Set16(central, 8, 8);
        Set32(central, 20, uint.MaxValue);
        Set32(central, 24, uint.MaxValue);
        Set16(central, 28, 1);
        Set32(central, 42, uint.MaxValue);
        central[46] = (byte)'a';
        if (zip64Sizes)
        {
            Set16(local, 28, 20);
            Set16(local, 31, 1);
            Set16(local, 33, 16);
            Set64(local, 35, size);
            Set64(local, 43, size);
            Set64(descriptor, 8, size);
            Set64(descriptor, 16, size);
        }
        else
        {
            Set32(descriptor, 8, uint.MaxValue);
            Set32(descriptor, 12, uint.MaxValue);
        }
        if (zip64Offset)
        {
            Set16(central, 30, 28);
            Set16(central, 47, 1);
            Set16(central, 49, 24);
            Set64(central, 51, size);
            Set64(central, 59, size);
            Set64(central, 67, offset);
        }

        var descriptorOffset = offset + local.Length + size;
        var directoryOffset = descriptorOffset + descriptor.Length;
        var endOffset = directoryOffset + central.Length;
        var end = CreateZip64(descriptor: false)[^98..];
        Set64(end, 24, 1);
        Set64(end, 32, 1);
        Set64(end, 40, central.Length);
        Set64(end, 48, directoryOffset);
        Set64(end, 64, endOffset);
        using var stream = new SparseZipStream(endOffset + end.Length,
            (offset, local), (descriptorOffset, descriptor), (directoryOffset, central), (endOffset, end));

        _ = new BacpacZipCopier(stream, None);
    }

    [Fact]
    public async Task Rewrite_discards_the_original_model_md5_extra_field()
    {
        using var scratch = NewScratch();
        var bytes = CreateZip(dataDescriptors: false);
        var (entries, directory) = IndexZip(bytes);
        var model = entries.Single(entry => entry.Name == ModelName);
        var local = model.Local + 30 + U16(bytes, model.Local + 26);
        var central = model.Central + 46 + U16(bytes, model.Central + 28);
        var md5 = new byte[20];
        Set16(md5, 0, 0x4b46);
        Set16(md5, 2, 16);
        Set16(bytes, model.Local + 28, (ushort)md5.Length);
        Set16(bytes, model.Central + 30, (ushort)md5.Length);
        foreach (var entry in entries.Where(entry => entry.Local > model.Local))
        {
            Set32(bytes, entry.Central + 42, (uint)(entry.Local + md5.Length));
        }
        Set32(bytes, bytes.Length - 10, U32(bytes, bytes.Length - 10) + (uint)md5.Length);
        Set32(bytes, bytes.Length - 6, (uint)(directory + md5.Length));
        await StageAsync(scratch, [.. bytes.AsSpan(0, local), .. md5, .. bytes.AsSpan(local, central - local),
            .. md5, .. bytes.AsSpan(central)]);
        Assert.True((await BacpacPackage.ValidateAsync(scratch.Package, None)).NeedsRewrite);

        await BacpacPackage.RewriteAsync(scratch, None);

        var copy = await File.ReadAllBytesAsync(scratch.RewrittenPackage);
        var rewritten = IndexZip(copy).Entries.Single(entry => entry.Name == ModelName);
        Assert.Equal(0, U16(copy, rewritten.Local + 28));
        Assert.Equal(0, U16(copy, rewritten.Central + 30));
        Assert.False((await BacpacPackage.ValidateAsync(scratch.RewrittenPackage, None)).NeedsRewrite);
    }

    [Fact]
    public async Task Preexisting_rewrite_spool_survives_failed_cleanup()
    {
        var scratch = NewScratch();
        var spool = Path.Combine(scratch.PathName, "rewritten-xml.tmp");
        try
        {
            await StageAsync(scratch, CreateZip(dataDescriptors: false));
            await File.WriteAllTextAsync(spool, "pre-existing");

            await Assert.ThrowsAsync<IOException>(() => BacpacPackage.RewriteAsync(scratch, None));
            var error = Assert.Throws<SqlBranchException>(scratch.Dispose);

            Assert.Equal(ErrorCodes.OperationFailed, error.Code);
            Assert.Contains("Could not verify ownership of client scratch", error.Message, StringComparison.Ordinal);
            Assert.Equal("pre-existing", await File.ReadAllTextAsync(spool));
        }
        finally
        {
            File.Delete(spool);
            scratch.Dispose();
        }
    }

    [Fact]
    public async Task Writes_zip64_end_records_when_entry_count_exceeds_classic_limit()
    {
        using var scratch = NewScratch();
        await using (var file = scratch.CreatePackage())
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            AddText(zip, ContentTypes, "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>");
            AddText(zip, OriginName, Origin);
            AddText(zip, ModelName, Model);
            for (var i = 0; i < ushort.MaxValue; ++i)
            {
                zip.CreateEntry($"Data/Part-{i}.BCP");
            }
        }

        await BacpacPackage.RewriteAsync(scratch, None);

        await using var rewritten = File.OpenRead(scratch.RewrittenPackage);
        var footer = new byte[98];
        rewritten.Seek(-footer.Length, SeekOrigin.End);
        await rewritten.ReadExactlyAsync(footer, None);
        Assert.Equal(0x06064b50u, U32(footer, 0));
        Assert.Equal((ulong)ushort.MaxValue + 3, U64(footer, 32));
        Assert.Equal(0x07064b50u, U32(footer, 56));
        Assert.Equal((ulong)(rewritten.Length - footer.Length), U64(footer, 64));
        Assert.Equal(0x06054b50u, U32(footer, 76));
        using var archive = new ZipArchive(rewritten, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(ushort.MaxValue + 3, archive.Entries.Count);
        Assert.False((await BacpacPackage.ValidateAsync(scratch.RewrittenPackage, None)).NeedsRewrite);
    }

    [Theory]
    [InlineData("encrypted local")]
    [InlineData("encrypted central")]
    [InlineData("strong encryption")]
    [InlineData("multi-disk EOCD")]
    [InlineData("multi-disk entry")]
    [InlineData("multi-disk Zip64 locator")]
    [InlineData("multi-disk Zip64 EOCD")]
    [InlineData("multi-disk Zip64 entry")]
    [InlineData("local name")]
    [InlineData("invalid UTF-8 name")]
    [InlineData("invalid UTF-8 comment")]
    [InlineData("local method")]
    [InlineData("local compressed size")]
    [InlineData("local uncompressed size")]
    [InlineData("local CRC")]
    [InlineData("overlap")]
    [InlineData("past directory")]
    [InlineData("duplicate")]
    [InlineData("bad descriptor")]
    [InlineData("descriptor local size")]
    public async Task Refuses_unsafe_records(string problem)
    {
        using var scratch = NewScratch();
        var zip64 = problem.Contains("Zip64", StringComparison.Ordinal);
        var dataDescriptors = problem.Contains("descriptor", StringComparison.Ordinal);
        var bytes = zip64 ? CreateZip64(descriptor: false) : CreateZip(dataDescriptors, duplicate: problem == "duplicate");
        Damage(bytes, problem);
        await StageAsync(scratch, bytes);

        var error = await Assert.ThrowsAsync<SqlBranchException>(() => BacpacPackage.RewriteAsync(scratch, None));

        Assert.Equal(ErrorCodes.BacpacInvalid, error.Code);
        Assert.False(File.Exists(scratch.RewrittenPackage));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(scratch.Package));
    }

    [Fact]
    public async Task Cancelled_rewrite_stops_between_raw_writes()
    {
        using var scratch = NewScratch();
        await StageAsync(scratch, CreateZip(dataDescriptors: false));
        using var cancellation = new CancellationTokenSource();
        using var sink = new MemoryStream();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BacpacPackage.RewriteAsync(scratch, cancellation.Token, _ => new NonSeekableOutput(sink, cancellation.Cancel)));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.InRange(sink.Length, 1, 256);
    }

    [BacpacPerfFact]
    public async Task Rewrite_and_validate_synthetic_gibibyte()
    {
        using var scratch = NewScratch();
        byte[] block = new byte[64 * 1024];
        // Fixed-width rows with mostly repeated fields and a high-entropy binary payload.
        var random = new Random(42);
        for (var row = 0; row < block.Length; row += 256)
        {
            "2026-11-20T08:00:00Z\tRetail\tCompleted\t"u8.CopyTo(block.AsSpan(row));
            random.NextBytes(block.AsSpan(row + 208, 48));
        }
        await using (var file = scratch.CreatePackage())
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            AddText(zip, ContentTypes, "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>");
            AddText(zip, OriginName, Origin);
            AddText(zip, ModelName, Model);
            for (var part = 0; part < 8; ++part)
            {
                await using var entry = zip.CreateEntry($"Data/dbo.Items/Part-{part}.BCP", CompressionLevel.Fastest).Open();
                for (var i = 0; i < 2048; ++i)
                {
                    // Vary the payload between blocks to approximate a large export.
                    random.NextBytes(block.AsSpan(0, 4096));
                    await entry.WriteAsync(block, None);
                }
            }
        }

        var bytes = new FileInfo(scratch.Package).Length;
        var timer = Stopwatch.StartNew();
        var detachment = await BacpacPackage.RewriteAsync(scratch, None);
        var rewrite = timer.Elapsed;
        var (_, needsRewrite) = await BacpacPackage.ValidateAsync(scratch.RewrittenPackage, None);
        timer.Stop();

        Assert.Equal(["app", "reader"], detachment.Users);
        Assert.False(needsRewrite);
        output.WriteLine(FormattableString.Invariant(
            $"{bytes / 1_000_000d:F2} MB source in {timer.Elapsed.TotalSeconds:F3} s ({bytes / 1_000_000d / timer.Elapsed.TotalSeconds:F2} MB/s) (rewrite {rewrite.TotalSeconds:F3} s, validation {(timer.Elapsed - rewrite).TotalSeconds:F3} s)"));
    }

    private static void AssertCopiedRecords(byte[] original, byte[] copy, bool descriptor, bool descriptorHasSignature = true)
    {
        var (originalEntries, _) = IndexZip(original);
        var (copiedEntries, _) = IndexZip(copy);
        Assert.Equal(originalEntries.Select(e => e.Name), copiedEntries.Select(e => e.Name));
        Assert.Equal(originalEntries.OrderBy(e => e.Local).Select(e => e.Name),
            copiedEntries.OrderBy(e => e.Local).Select(e => e.Name));
        foreach (var (before, after) in originalEntries.Zip(copiedEntries))
        {
            if (before.Name is ModelName or OriginName)
            {
                Assert.Equal(8, U16(copy, after.Local + 8));
                continue;
            }
            var localLength = 30 + U16(original, before.Local + 26) + U16(original, before.Local + 28);
            var dataEnd = before.Local + localLength + before.Compressed;
            var usesDescriptor = (U16(original, before.Local + 6) & 8) != 0;
            if (before.Name == DataName)
            {
                Assert.Equal(descriptor, usesDescriptor);
            }
            var descriptorLength = usesDescriptor ? (before.Zip64 ? 20 : 12) + (descriptorHasSignature ? 4 : 0) : 0;
            if (usesDescriptor && descriptorHasSignature)
            {
                Assert.Equal(0x08074b50u, U32(original, dataEnd));
            }
            var recordLength = localLength + before.Compressed + descriptorLength;
            Assert.Equal(original.AsSpan(before.Local, recordLength).ToArray(), copy.AsSpan(after.Local, recordLength).ToArray());
            Assert.Equal(U32(original, before.Central + 16), U32(copy, after.Central + 16));

            var centralLength = 46 + U16(original, before.Central + 28) + U16(original, before.Central + 30) + U16(original, before.Central + 32);
            var beforeRecord = original.AsSpan(before.Central, centralLength).ToArray();
            var afterRecord = copy.AsSpan(after.Central, centralLength).ToArray();
            var offsetAt = before.Zip64 ? 46 + U16(original, before.Central + 28) + 4 + 16 : 42;
            beforeRecord.AsSpan(offsetAt, before.Zip64 ? 8 : 4).Clear();
            afterRecord.AsSpan(offsetAt, before.Zip64 ? 8 : 4).Clear();
            Assert.Equal(beforeRecord, afterRecord);
        }
    }

    private static byte[] CreateZip(bool dataDescriptors, bool duplicate = false)
    {
        using var buffer = new MemoryStream();
        using var nonSeekable = new NonSeekableOutput(buffer);
        using (var zip = new ZipArchive(dataDescriptors ? nonSeekable : buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(zip, ContentTypes, "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>");
            AddText(zip, OriginName, Origin);
            AddText(zip, DataName, string.Concat(Enumerable.Repeat("1001\tRetail\tCompleted\n", 64)));
            AddText(zip, ModelName, Model);
            AddText(zip, "Data/Größe.BCP", "uncompressed", CompressionLevel.NoCompression);
            if (duplicate)
            {
                AddText(zip, DataName.ToLowerInvariant(), "duplicate");
            }
        }
        return buffer.ToArray();
    }

    private static void AddText(ZipArchive zip, string name, string text, CompressionLevel compression = CompressionLevel.Fastest)
    {
        var entry = zip.CreateEntry(name, compression);
        entry.LastWriteTime = Timestamp;
        entry.ExternalAttributes = 0x20;
        entry.Comment = "entry comment";
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static byte[] CreateZip64(bool descriptor)
    {
        // Convert small, ordinary records into ZIP64 records, with a signatureless 64-bit
        // descriptor for the BCP entry and a central directory ordered differently on disk.
        var original = CreateZip(dataDescriptors: false);
        var (entries, _) = IndexZip(original);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Name != "Data/Größe.BCP"))
        {
            offsets.Add(entry.Name, stream.Position);
            var local = original.AsSpan(entry.Local, 30).ToArray();
            var size = U32(local, 22);
            var compressed = (uint)entry.Compressed;
            var crc = U32(local, 14);
            var hasDescriptor = descriptor && entry.Name == DataName;
            Set16(local, 4, 45);
            if (hasDescriptor)
            {
                Set16(local, 6, (ushort)(U16(local, 6) | 8));
                Set32(local, 14, 0);
            }
            Set32(local, 18, uint.MaxValue);
            Set32(local, 22, uint.MaxValue);
            Set16(local, 28, 20);
            var nameLength = U16(local, 26);
            writer.Write(local);
            writer.Write(original.AsSpan(entry.Local + 30, nameLength));
            writer.Write((ushort)1);
            writer.Write((ushort)16);
            writer.Write(hasDescriptor ? 0UL : size);
            writer.Write(hasDescriptor ? 0UL : compressed);
            writer.Write(original.AsSpan(entry.Local + 30 + nameLength, entry.Compressed));
            if (hasDescriptor)
            {
                writer.Write(crc);
                writer.Write((ulong)compressed);
                writer.Write((ulong)size);
            }
        }
        var directory = stream.Position;
        foreach (var name in new[] { ContentTypes, DataName, OriginName, ModelName })
        {
            var entry = entries.Single(e => e.Name == name);
            var central = original.AsSpan(entry.Central, 46).ToArray();
            var nameLength = U16(central, 28);
            var commentLength = U16(central, 32);
            var size = U32(central, 24);
            Set16(central, 6, 45);
            if (descriptor && entry.Name == DataName)
            {
                Set16(central, 8, (ushort)(U16(central, 8) | 8));
            }
            Set32(central, 20, uint.MaxValue);
            Set32(central, 24, uint.MaxValue);
            Set16(central, 30, 32);
            Set16(central, 34, ushort.MaxValue);
            Set32(central, 42, uint.MaxValue);
            writer.Write(central);
            writer.Write(original.AsSpan(entry.Central + 46, nameLength));
            writer.Write((ushort)1);
            writer.Write((ushort)28);
            writer.Write((ulong)size);
            writer.Write((ulong)entry.Compressed);
            writer.Write(offsets[entry.Name]);
            writer.Write(0u); // Starting disk
            writer.Write(original.AsSpan(entry.Central + 46 + nameLength, commentLength));
        }
        var zip64End = stream.Position;
        writer.Write(0x06064b50u);
        writer.Write(44L);
        writer.Write((ushort)45);
        writer.Write((ushort)45);
        writer.Write(0L); // Both disk numbers
        writer.Write((long)offsets.Count);
        writer.Write((long)offsets.Count);
        writer.Write(zip64End - directory);
        writer.Write(directory);
        writer.Write(0x07064b50u);
        writer.Write(0u);
        writer.Write(zip64End);
        writer.Write(1u);
        writer.Write(0x06054b50u);
        writer.Write(uint.MaxValue); // Both classic disk numbers are ZIP64 sentinels.
        writer.Write(ushort.MaxValue);
        writer.Write(ushort.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write((ushort)0);
        return stream.ToArray();
    }

    private static void Damage(byte[] bytes, string problem)
    {
        var (entries, directory) = IndexZip(bytes);
        var data = entries.First(e => e.Name == DataName);
        var last = entries[^1];
        switch (problem)
        {
            case "encrypted local": Set16(bytes, data.Local + 6, 1); break;
            case "encrypted central": Set16(bytes, data.Central + 8, 1); break;
            case "strong encryption": Set16(bytes, data.Central + 8, 1 << 6); break;
            case "multi-disk EOCD": Set16(bytes, bytes.Length - 18, 1); break;
            case "multi-disk entry": Set16(bytes, data.Central + 34, 1); break;
            case "multi-disk Zip64 locator": Set32(bytes, bytes.Length - 26, 2); break;
            case "multi-disk Zip64 EOCD": Set32(bytes, bytes.Length - 82, 1); break;
            case "multi-disk Zip64 entry": Set32(bytes, data.Central + 46 + DataName.Length + 4 + 24, 1); break;
            case "local name": bytes[data.Local + 30] ^= 1; break;
            case "invalid UTF-8 name":
                Set16(bytes, data.Local + 6, (ushort)(U16(bytes, data.Local + 6) | (1 << 11)));
                Set16(bytes, data.Central + 8, (ushort)(U16(bytes, data.Central + 8) | (1 << 11)));
                bytes[data.Local + 30] = bytes[data.Central + 46] = 0xff;
                break;
            case "invalid UTF-8 comment":
                Set16(bytes, data.Local + 6, (ushort)(U16(bytes, data.Local + 6) | (1 << 11)));
                Set16(bytes, data.Central + 8, (ushort)(U16(bytes, data.Central + 8) | (1 << 11)));
                bytes[data.Central + 46 + U16(bytes, data.Central + 28) + U16(bytes, data.Central + 30)] = 0xff;
                break;
            case "local method": Set16(bytes, data.Local + 8, 0); break;
            case "local compressed size": Set32(bytes, data.Local + 18, 1); break;
            case "local uncompressed size": Set32(bytes, data.Local + 22, 1); break;
            case "local CRC": bytes[data.Local + 14] ^= 1; break;
            case "overlap":
                Set32(bytes, data.Local + 18, (uint)data.Compressed + 1);
                Set32(bytes, data.Central + 20, (uint)data.Compressed + 1);
                break;
            case "past directory":
                var oversized = (uint)(directory - last.Local);
                Set32(bytes, last.Local + 18, oversized);
                Set32(bytes, last.Central + 20, oversized);
                break;
            case "duplicate": break;
            case "bad descriptor": bytes[data.Local + 30 + U16(bytes, data.Local + 26) + data.Compressed + 4] ^= 1; break;
            case "descriptor local size": Set32(bytes, data.Local + 22, 1); break;
            default: throw new ArgumentException(problem);
        }
    }

    private static (Record[] Entries, int Directory) IndexZip(byte[] bytes)
    {
        var end = bytes.Length - 22;
        Assert.Equal(0x06054b50u, U32(bytes, end));
        var directory = U32(bytes, end + 16) == uint.MaxValue
            ? (int)U64(bytes, end - 20 - 56 + 48) : (int)U32(bytes, end + 16);
        var records = new List<Record>();
        for (var central = directory; U32(bytes, central) == 0x02014b50u;)
        {
            var nameLength = U16(bytes, central + 28);
            var extra = central + 46 + nameLength;
            var zip64 = U32(bytes, central + 42) == uint.MaxValue;
            var offset = zip64 ? (int)U64(bytes, extra + 20) : (int)U32(bytes, central + 42);
            var compressed = zip64 ? (int)U64(bytes, extra + 12) : (int)U32(bytes, central + 20);
            records.Add(new Record(Encoding.UTF8.GetString(bytes, central + 46, nameLength), central, offset, compressed, zip64));
            central = extra + U16(bytes, central + 30) + U16(bytes, central + 32);
        }
        return (records.ToArray(), directory);
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static void Set16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Set32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Set64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);

    private static BacpacScratch NewScratch() => new("l" + Guid.NewGuid().ToString("N"), "zip-unit");

    private static async Task<byte[]> StageAsync(BacpacScratch scratch, byte[] bytes)
    {
        await using var package = scratch.CreatePackage();
        await package.WriteAsync(bytes, None);
        return bytes;
    }

    private sealed record Record(string Name, int Central, int Local, int Compressed, bool Zip64);

    private sealed class SparseZipStream(long length, params (long Offset, byte[] Bytes)[] records) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - Position);
            buffer[..count].Clear();
            foreach (var (offset, bytes) in records)
            {
                var start = Math.Max(Position, offset);
                var end = Math.Min(Position + count, offset + bytes.Length);
                if (start < end)
                {
                    bytes.AsSpan((int)(start - offset), (int)(end - start)).CopyTo(buffer[(int)(start - Position)..]);
                }
            }
            Position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableOutput(Stream stream, Action? afterWrite = null) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await stream.WriteAsync(buffer, cancellationToken);
            afterWrite?.Invoke();
        }
    }
}

internal sealed class BacpacPerfFactAttribute : FactAttribute
{
    public BacpacPerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SQLBRANCH_PERF") != "1")
        {
            Skip = "Set SQLBRANCH_PERF=1 to run the BACPAC timing test.";
        }
    }
}

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>One entry of an archive a real export would never write.</summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Data">Its bytes; for a link, ignored.</param>
/// <param name="Type">Its tar entry type.</param>
internal sealed record CraftEntry(string Name, byte[] Data, TarEntryType Type = TarEntryType.RegularFile)
{
    public static CraftEntry Text(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));
}

/// <summary>Builds image archives for tests: valid ones with the real writer, and crafted ones, entry by entry.</summary>
internal static class ArchiveCrafter
{
    public static ImageManifest Manifest(string name = "shop", params ImageFile[] files) => new(
        ImageManifest.CurrentFormat, name, "Shop", "16.0.4135.4", files, "FULL", "SIMPLE", 268_435_456, LogRebuilt: true,
        new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero), "shop.bak", new string('a', 64), new Dictionary<string, string>(),
        "mcr.microsoft.com/mssql/server:2022-latest");

    /// <summary>A valid archive written by the real writer.</summary>
    public static async Task<byte[]> ValidAsync(ImageManifest manifest, params byte[][] contents)
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

    /// <summary>The head entry of a manifest, as the writer makes it.</summary>
    public static CraftEntry Head(ImageManifest manifest) => new(ImageArchive.HeadEntry, HeadBytes(manifest));

    private static byte[] HeadBytes(ImageManifest manifest) =>
        Encoding.UTF8.GetBytes($$"""{ "archiveFormat": 1, "manifest": {{manifest.ToJson()}} }""");

    /// <summary>The checksum entry that matches the head and the given contents (or another list).</summary>
    public static CraftEntry Checksums(ImageManifest manifest, params byte[][] contents)
    {
        var head = HeadBytes(manifest);
        var files = manifest.Files.Select((f, i) =>
            new ArchiveFileChecksum(f.FileName, contents[i].Length, Convert.ToHexStringLower(SHA256.HashData(contents[i])))).ToList();
        return new CraftEntry(ImageArchive.ChecksumsEntry,
            Encoding.UTF8.GetBytes(new ArchiveChecksums(Convert.ToHexStringLower(SHA256.HashData(head)), files).ToJson()));
    }

    /// <summary>A gzip stream of a PAX tar with exactly these entries.</summary>
    public static byte[] Craft(params CraftEntry[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                if (entry.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    tar.WriteEntry(new PaxTarEntry(entry.Type, entry.Name) { LinkName = "/etc/passwd" });
                }
                else
                {
                    tar.WriteEntry(new PaxTarEntry(entry.Type, entry.Name) { DataStream = new MemoryStream(entry.Data) });
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>A whole crafted archive: head, the given data entries, and matching checksums unless <paramref name="checksums"/> says otherwise.</summary>
    public static byte[] CraftFull(ImageManifest manifest, IEnumerable<CraftEntry> data, CraftEntry? checksums)
    {
        var all = new List<CraftEntry> { Head(manifest) };
        all.AddRange(data);
        if (checksums is not null)
        {
            all.Add(checksums);
        }

        return Craft([.. all]);
    }
}

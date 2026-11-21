using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images.Archive;

/// <summary>
/// The image archive of design D2: a zstd stream (gzip in older exports) of a PAX tar holding, in this order, <c>sqlbranch-archive.json</c>,
/// <c>data/&lt;fileName&gt;</c> for each file in manifest order, and <c>sqlbranch-checksums.json</c>.
/// </summary>
public static class ImageArchive
{
    /// <summary>The archive format this version writes and the newest it reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The first entry: the archive format and the image's manifest.</summary>
    public const string HeadEntry = "sqlbranch-archive.json";

    /// <summary>The last entry: the SHA-256 and size of each data file and the SHA-256 of the first entry.</summary>
    public const string ChecksumsEntry = "sqlbranch-checksums.json";

    /// <summary>The folder name of the data entries.</summary>
    public const string DataPrefix = "data/";

    /// <summary>The suggested file extension. None is required.</summary>
    public const string SuggestedExtension = ".sbimg";

    /// <summary>The most bytes a head or checksums entry may hold; a real one is a few kilobytes.</summary>
    public const long MaxMetadataBytes = 16L << 20;

    /// <summary>The first bytes of a gzip stream.</summary>
    public static ReadOnlySpan<byte> GzipMagic => [0x1f, 0x8b];

    /// <summary>The first bytes of a zstd frame.</summary>
    public static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xb5, 0x2f, 0xfd];

    /// <summary>The entry name of a data file.</summary>
    public static string DataEntry(string fileName) => DataPrefix + fileName;
}

/// <summary>The first entry of an archive, read.</summary>
/// <param name="ArchiveFormat">The archive format number.</param>
/// <param name="Manifest">The image manifest as exported. It is untrusted until import has checked it.</param>
/// <param name="Sha256">The SHA-256 of the entry's bytes, lower-case hex.</param>
public sealed record ArchiveHead(int ArchiveFormat, ImageManifest Manifest, string Sha256);

/// <summary>The size and SHA-256 of one data file.</summary>
public sealed record ArchiveFileChecksum(string FileName, long SizeBytes, string Sha256);

/// <summary>The last entry of an archive: what the data files must hash to, and the hash of the head.</summary>
public sealed record ArchiveChecksums(string ManifestSha256, IReadOnlyList<ArchiveFileChecksum> Files)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads the checksum list, or throws exit 1 <c>archive_corrupt</c> when it is not one.</summary>
    public static ArchiveChecksums Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ArchiveChecksums>(json, Options);
            if (parsed is { ManifestSha256: not null, Files: not null })
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Reported below.
        }

        throw new SqlBranchException(ErrorCodes.ArchiveCorrupt, $"The archive's {ImageArchive.ChecksumsEntry} cannot be read.");
    }
}

/// <summary>How the head entry is made and read.</summary>
internal static class ArchiveHeadJson
{
    public static byte[] Write(ImageManifest manifest)
    {
        var node = new JsonObject
        {
            ["archiveFormat"] = ImageArchive.CurrentFormat,
            ["manifest"] = JsonNode.Parse(manifest.ToJson()),
        };
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Reads the head, or throws exit 1 <c>archive_invalid</c>. The format is checked before the manifest is looked at.</summary>
    public static ArchiveHead Read(byte[] bytes, string sha256)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw Invalid($"its {ImageArchive.HeadEntry} is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("archiveFormat", out var format) ||
                format.ValueKind != JsonValueKind.Number ||
                !format.TryGetInt32(out var number) || number < 1)
            {
                throw Invalid($"its {ImageArchive.HeadEntry} has no archive format.");
            }

            if (number > ImageArchive.CurrentFormat)
            {
                throw Invalid(
                    $"it uses archive format {number.ToString(CultureInfo.InvariantCulture)}, which a newer SqlBranch wrote; this version reads format {ImageArchive.CurrentFormat.ToString(CultureInfo.InvariantCulture)}. Update SqlBranch to import it.");
            }

            if (!root.TryGetProperty("manifest", out var manifestElement) || manifestElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"its {ImageArchive.HeadEntry} has no manifest.");
            }

            ImageManifest manifest;
            try
            {
                manifest = ImageManifest.FromJson(manifestElement.GetRawText());
            }
            catch (JsonException)
            {
                throw Invalid("its manifest cannot be read.");
            }

            if (manifest.Format != ImageManifest.CurrentFormat || manifest.Files is null || manifest.Name is null)
            {
                throw Invalid(manifest.Format > ImageManifest.CurrentFormat
                    ? "its manifest was written by a newer SqlBranch."
                    : "its manifest has an unknown format.");
            }

            return new ArchiveHead(number, manifest, sha256);
        }
    }

    public static SqlBranchException Invalid(string reason) =>
        new(ErrorCodes.ArchiveInvalid, $"The source is not an image archive this version can read: {reason}");
}

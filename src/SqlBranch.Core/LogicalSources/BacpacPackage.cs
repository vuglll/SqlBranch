using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.LogicalSources;

/// <summary>Checks package bytes without extracting files or executing model SQL.</summary>
internal static class BacpacPackage
{
    internal const string ModelNamespace = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";
    private static readonly XNamespace Ns = ModelNamespace;

    public static async Task<(long Length, bool NeedsRewrite)> ValidateAsync(string path, CancellationToken token)
    {
        try
        {
            await using var file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
            if (!entries.TryGetValue("[Content_Types].xml", out var contentTypes) || !entries.TryGetValue("Origin.xml", out var origin) ||
                !entries.TryGetValue("model.xml", out var model) || !HasContentTypes(contentTypes))
            {
                throw Invalid();
            }

            var modelChecksum = ReadExportMetadata(origin);
            await CheckEntriesAsync(zip, model, modelChecksum, token).ConfigureAwait(false);
            var needsRewrite = InspectModel(model, token);
            if (entries.Keys.Any(IsExecutablePackagePart))
            {
                throw new SqlBranchException(ErrorCodes.UnsupportedFeature, "BACPAC import does not support deployment scripts or contributors embedded in a package.");
            }
            return (file.Length, needsRewrite);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or ArgumentException or FormatException)
        {
            throw Invalid();
        }
    }

    private static XmlReader Reader(Stream stream, long maxCharacters = 0, bool preserveWhitespace = false, bool useAsync = false) => XmlReader.Create(stream, new XmlReaderSettings
    {
        Async = useAsync,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = !preserveWhitespace,
        IgnoreWhitespace = !preserveWhitespace,
        MaxCharactersInDocument = maxCharacters,
    });

    private static XElement ReadMetadata(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var xml = Reader(stream, maxCharacters: 16_000_000);
        return XDocument.Load(xml).Root ?? throw Invalid();
    }

    private static byte[]? ReadExportMetadata(ZipArchiveEntry entry)
    {
        var root = ReadMetadata(entry);
        if (root.Name != Ns + "DacOrigin" ||
            (string?)root.Element(Ns + "PackageProperties")?.Element(Ns + "ContainsExportedData") is not ("true" or "1"))
        {
            throw Invalid();
        }

        byte[]? modelChecksum = null;
        foreach (var checksum in ModelChecksums(root))
        {
            var bytes = Convert.FromHexString(checksum.Value);
            if (bytes.Length != SHA256.HashSizeInBytes ||
                (modelChecksum is not null && !bytes.AsSpan().SequenceEqual(modelChecksum)))
            {
                throw Invalid();
            }
            modelChecksum = bytes;
        }
        return modelChecksum;
    }

    private static bool HasContentTypes(ZipArchiveEntry entry) =>
        ReadMetadata(entry).Name == XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types");

    private static IEnumerable<XElement> ModelChecksums(XElement origin) =>
        origin.Elements(Ns + "Checksums").Elements(Ns + "Checksum")
            .Where(c => string.Equals((string?)c.Attribute("Uri"), "/model.xml", StringComparison.OrdinalIgnoreCase));

    private static bool InspectModel(ZipArchiveEntry entry, CancellationToken token)
    {
        using var stream = entry.Open();
        using var xml = Reader(stream);
        if (xml.MoveToContent() != XmlNodeType.Element || xml.LocalName != "DataSchemaModel" ||
            xml.NamespaceURI != ModelNamespace || !xml.ReadToDescendant("Model", ModelNamespace))
        {
            throw Invalid();
        }

        var needsRewrite = false;
        var modelDepth = xml.Depth;
        while (xml.Read())
        {
            token.ThrowIfCancellationRequested();
            if (xml.NodeType != XmlNodeType.Element || xml.Depth != modelDepth + 1 ||
                xml.LocalName != "Element" || xml.NamespaceURI != ModelNamespace)
            {
                continue;
            }

            switch (xml.GetAttribute("Type"))
            {
                case "SqlFile":
                    throw new SqlBranchException(ErrorCodes.UnsupportedFeature, "BACPAC import does not support package-defined database files.");
                case "SqlLogin":
                    needsRewrite = true;
                    break;
                case "SqlUser":
                    using (var subtree = xml.ReadSubtree())
                    {
                        needsRewrite |= BacpacModelRewriter.IsMappedUser(XElement.Load(subtree));
                    }
                    break;
            }
        }
        return needsRewrite;
    }

    // The caller validates the staged original before requesting a rewrite.
    public static async Task<BacpacDetachment> RewriteAsync(BacpacScratch scratch, CancellationToken token,
        Func<Stream, Stream>? wrapOutput = null)
    {
        await using var file = File.OpenRead(scratch.Package);
        var copier = new BacpacZipCopier(file, token);
        using var source = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var modelEntry = source.Entries.FirstOrDefault(e => e.FullName.Equals("model.xml", StringComparison.OrdinalIgnoreCase)) ?? throw Invalid();
        var originEntry = source.Entries.FirstOrDefault(e => e.FullName.Equals("Origin.xml", StringComparison.OrdinalIgnoreCase)) ?? throw Invalid();
        XDocument model;
        await using (var input = modelEntry.Open())
        using (var xml = Reader(input, preserveWhitespace: true, useAsync: true))
        {
            model = await XDocument.LoadAsync(xml, LoadOptions.PreserveWhitespace, token).ConfigureAwait(false);
        }

        var result = BacpacModelRewriter.Detach(model.Root?.Element(Ns + "Model") ?? throw Invalid(), token);
        var origin = ReadMetadata(originEntry);
        var checksums = ModelChecksums(origin).ToArray();

        // Stage only the compressed XML so both sizes are known before writing each local header.
        await using var spool = scratch.CreateRewriteSpool();
        var newModel = await BacpacZipCopier.CompressAsync(spool, async output =>
        {
            if (checksums.Length > 0)
            {
                using var sha256 = SHA256.Create();
                await using var hashing = new CryptoStream(output, sha256, CryptoStreamMode.Write, leaveOpen: true);
                await model.SaveAsync(hashing, SaveOptions.DisableFormatting, token).ConfigureAwait(false);
                await hashing.FlushFinalBlockAsync(token).ConfigureAwait(false);
                var checksum = Convert.ToHexString(sha256.Hash!);
                foreach (var element in checksums)
                {
                    element.Value = checksum;
                }
            }
            else
            {
                await model.SaveAsync(output, SaveOptions.DisableFormatting, token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        var newOrigin = await BacpacZipCopier.CompressAsync(spool,
            output => origin.SaveAsync(output, SaveOptions.DisableFormatting, token)).ConfigureAwait(false);

        await using var ownedFile = scratch.CreateRewrittenPackage();
        await using var wrapped = wrapOutput?.Invoke(ownedFile);
        await copier.WriteAsync(wrapped ?? ownedFile, spool, newModel, newOrigin, token).ConfigureAwait(false);
        return result;
    }

    private static async Task CheckEntriesAsync(ZipArchive zip, ZipArchiveEntry model, byte[]? modelChecksum, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var entry in zip.Entries)
            {
                var name = Uri.UnescapeDataString(entry.FullName);
                if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
                    name.Split('/').Any(segment => segment is "." or ".."))
                {
                    throw Invalid();
                }
                using var hash = ReferenceEquals(entry, model) && modelChecksum is not null
                    ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                var crc = uint.MaxValue;
                long length = 0;
                await using var content = entry.Open();
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                {
                    length += read;
                    hash?.AppendData(buffer, 0, read);
                    crc = BacpacCrc32.Update(crc, buffer.AsSpan(0, read));
                    if (length > entry.Length)
                    {
                        throw Invalid();
                    }
                }
                if (length != entry.Length || ~crc != entry.Crc32 ||
                    (hash is not null && !hash.GetHashAndReset().AsSpan().SequenceEqual(modelChecksum)))
                {
                    throw Invalid();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsExecutablePackagePart(string name) =>
        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    internal static SqlBranchException Invalid() =>
        new(ErrorCodes.BacpacInvalid, "The input is not a valid BACPAC package.");
}

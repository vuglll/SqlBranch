using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Images;

/// <summary>One image as <c>image list</c> shows it.</summary>
/// <param name="Name">The image's folder name, which is the name the commands use.</param>
/// <param name="Manifest">The image's manifest, or null when an unreadable catalog has no readable top-level manifest.</param>
/// <param name="Branches">The names of the branches created from it, in order.</param>
public sealed record ImageEntry(string Name, ImageManifest? Manifest, IReadOnlyList<string> Branches)
{
    /// <summary>The current version's ID on a format-2 pool; null on format 1 and for an image no command has adopted yet (D3a).</summary>
    public string? Version { get; init; }

    /// <summary>False for an unreadable catalog, true for other format-2 rows, null on format 1.</summary>
    public bool? CatalogReadable { get; init; }

    /// <summary>The folder of the version's files, relative to the image folder.</summary>
    public string Directory { get; init; } = ".";

    /// <summary>Every version ID the catalog lists; empty on format 1 and for an image not adopted yet.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];
}

/// <summary>A version read from the pool: its ID (null for an image no command adopted yet), its folder relative to the image folder, and its manifest.</summary>
public sealed record ImageVersionData(string? Id, string Directory, ImageManifest Manifest);

/// <summary>What the pool holds: the images, and a note for each folder that looked like one but could not be read.</summary>
public sealed record ImageListing(IReadOnlyList<ImageEntry> Images, IReadOnlyList<string> Warnings)
{
    /// <summary>The pool format the listing was read under: 1 or 2.</summary>
    public int PoolFormat { get; init; } = 1;
}

/// <summary>
/// Reads the images in the pool and counts the branches created from each (design D3: the pool is the state).
/// An image exists once its <c>manifest.json</c> does; <c>.removing-*</c> folders are not images. A branch counts
/// for an image when its <c>branch.json</c> names it in <c>image</c>. Only the engine container reads the pool.
/// </summary>
public sealed class ImageCatalog
{
    private const string ImageTag = "SQLBRANCH_IMAGE";
    private const string BranchTag = "SQLBRANCH_BRANCHJSON";
    private const string CatalogTag = "SQLBRANCH_CATALOG";
    private const string VersionManifestTag = "SQLBRANCH_VMANIFEST";
    private const string CredentialsTag = "SQLBRANCH_POOLCRED";

    /// <summary>
    /// Prints one tagged line per image (name and manifest as base64) and one per branch (name and
    /// <c>branch.json</c> as base64). The glob skips folders that start with a dot, such as <c>.removing-*</c>.
    /// </summary>
    public const string ListingScript =
        "if [ -f /pool/engine/credentials.json ]; then g=$(grep -m1 -o '\"format\"[[:space:]]*:[[:space:]]*[0-9]*' /pool/engine/credentials.json 2>/dev/null); " +
        "if [ $? -ge 2 ]; then echo \"SQLBRANCH_POOLCRED|unreadable\"; else n=$(printf %s \"$g\" | tr -cd 0-9); echo \"SQLBRANCH_POOLCRED|${n:-1}\"; fi; fi; " +
        "[ -d /pool/images/.ids ] && echo SQLBRANCH_FORMAT2CONTENT; " +
        "for d in /pool/images/*/; do n=$(basename \"$d\"); " +
        "[ -f \"${d}catalog.json\" ] && echo \"SQLBRANCH_CATALOG|$n|$(base64 -w0 \"${d}catalog.json\")\"; " +
        "for v in \"${d}\"versions/*/; do [ -f \"${v}manifest.json\" ] && echo \"SQLBRANCH_VMANIFEST|$n|versions/$(basename \"$v\")|$(base64 -w0 \"${v}manifest.json\")\"; done; " +
        "[ -f \"${d}manifest.json\" ] || continue; " +
        "echo \"SQLBRANCH_IMAGE|$n|$(base64 -w0 \"${d}manifest.json\")\"; done; " +
        "for d in /pool/branches/*/; do [ -f \"${d}branch.json\" ] || continue; " +
        "echo \"SQLBRANCH_BRANCHJSON|$(basename \"$d\")|$(base64 -w0 \"${d}branch.json\")\"; done; true";

    private readonly IContainerRuntime _runtime;
    private readonly string _engineName;

    public ImageCatalog(IContainerRuntime runtime, string engineName = ContainerLabels.EngineName)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _engineName = engineName;
    }

    /// <summary>The engine of the pool, which must be running because the pool is read through it.</summary>
    public static ContainerInfo RequireRunningEngine(FoundPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var engine = pool.Engine ?? throw new SqlBranchException(ErrorCodes.NotInitialized,
            $"The pool {pool.Location} exists but the engine container does not. Run 'sqlbranch init' to create the engine again.");
        if (!engine.Running)
        {
            throw new SqlBranchException(ErrorCodes.EngineStopped, "The engine is stopped. Run 'sqlbranch engine start' to start it.");
        }

        return engine;
    }

    /// <summary>
    /// The images of the pool. On a format-2 pool each name is read from its catalog (design D3); on format 1 the code path is
    /// the one of 0.2.2. The format comes from the credentials file the same exec reads, and only its number is kept.
    /// A format above 2 is refused with <c>operation_failed</c>. A caller that already holds the parsed credentials passes them in, so the file is read once per command.
    /// </summary>
    public async Task<ImageListing> ListAsync(CancellationToken cancellationToken, PoolLocation? pool = null, CredentialsFile? credentials = null)
    {
        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", ListingScript], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Could not list the images in the pool (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
        }

        var warnings = new List<string>();
        var manifests = new SortedDictionary<string, ImageManifest>(StringComparer.Ordinal);
        var branchImages = new List<(string Branch, string? Image)>();
        var catalogs = new Dictionary<string, string>(StringComparer.Ordinal);
        var versionManifests = new Dictionary<(string Name, string Dir), string>();
        var formatHint = "1";

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 4);
            if (parts.Length < 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case CredentialsTag:
                    formatHint = parts[1];
                    break;
                case CatalogTag when parts.Length >= 3:
                    catalogs[parts[1]] = parts[2];
                    break;
                case VersionManifestTag when parts.Length == 4:
                    versionManifests[(parts[1], parts[2])] = parts[3];
                    break;
                case ImageTag when parts.Length >= 3:
                    // A folder that is not a valid image name is not one of ours, even with a manifest in it.
                    if (NameRules.Problem(parts[1], forBranch: false) is not null)
                    {
                        break;
                    }

                    if (TryParseManifest(parts[2]) is { } manifest)
                    {
                        manifests[parts[1]] = manifest;
                    }
                    else
                    {
                        warnings.Add($"The manifest of the image folder {ImagePaths.Image(parts[1])} cannot be read, so the image is not listed.");
                    }

                    break;
                case BranchTag when parts.Length >= 3:
                    branchImages.Add((parts[1], ImageOfBranch(parts[2])));
                    break;
                default:
                    break;
            }
        }

        int format;
        // A format hint from grep cannot validate JSON. With format-2 content, use the
        // credentials reader so damaged credentials cannot silently expose a format-1 view.
        if (credentials is not null)
        {
            // The caller already read and parsed the file; it is not read again.
            format = credentials.Format;
        }
        else if (pool is not null && (catalogs.Count > 0 || result.StdOut.Contains("SQLBRANCH_FORMAT2CONTENT", StringComparison.Ordinal)))
        {
            var engine = await _runtime.InspectAsync(_engineName, cancellationToken).ConfigureAwait(false)
                ?? throw new SqlBranchException(ErrorCodes.OperationFailed, "The engine container could not be found.");
            var read = await EngineCredentials.ReadFileAsync(_runtime, engine.Image, pool, cancellationToken).ConfigureAwait(false);
            format = read?.Format ?? FormatOf(formatHint, pool);
        }
        else
        {
            format = FormatOf(formatHint, pool);
        }

        var entries = new List<ImageEntry>();
        if (format < 2)
        {
            entries.AddRange(manifests.Select(m => new ImageEntry(m.Key, m.Value, BranchesOf(branchImages, m.Key))));
        }
        else
        {
            foreach (var name in manifests.Keys.Concat(catalogs.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (NameRules.Problem(name, forBranch: false) is not null)
                {
                    continue;
                }

                if (!catalogs.TryGetValue(name, out var catalogText))
                {
                    // Written by an older client after the upgrade and not adopted yet (D3a): no version ID.
                    if (manifests.TryGetValue(name, out var legacy))
                    {
                        entries.Add(new ImageEntry(name, legacy, BranchesOf(branchImages, name)) { CatalogReadable = true });
                    }

                    continue;
                }

                var catalog = TryParseCatalog(catalogText);
                if (catalog is null)
                {
                    entries.Add(new ImageEntry(name, manifests.GetValueOrDefault(name), BranchesOf(branchImages, name)) { CatalogReadable = false });
                    continue;
                }

                if (catalog.Current is null)
                {
                    continue;
                }

                var directory = catalog.CurrentDirectory;
                var manifest = directory switch
                {
                    null => null,
                    "." => manifests.GetValueOrDefault(name),
                    _ => versionManifests.TryGetValue((name, directory), out var text) ? TryParseManifest(text) : null,
                };
                if (manifest is null)
                {
                    warnings.Add($"The current version of the image {name} has no readable manifest, so the image is not listed.");
                    continue;
                }

                entries.Add(new ImageEntry(name, manifest, BranchesOf(branchImages, name)) { CatalogReadable = true, Version = catalog.Current, Directory = directory!, Versions = [.. catalog.Versions.Select(v => v.Id)] });
            }
        }

        return new ImageListing(entries, warnings) { PoolFormat = format };
    }

    private static string[] BranchesOf(List<(string Branch, string? Image)> branchImages, string image) =>
        [.. branchImages.Where(b => b.Image == image).Select(b => b.Branch).Order(StringComparer.Ordinal)];

    private static ImageCatalogFile? TryParseCatalog(string base64)
    {
        try
        {
            var catalog = ImageCatalogFile.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            return catalog is not null && catalog.Versions.All(v => v.Dir == "." || v.Dir == $"versions/{v.Id}") ? catalog : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The format number the listing script extracted from the credentials file. An unreadable file counts as format 1, as
    /// 0.2.2 never read it for <c>image list</c>.
    /// </summary>
    private static int FormatOf(string value, PoolLocation? pool)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var format))
        {
            return 1;
        }

        if (format > EngineCredentials.NewestFormat)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The pool {pool?.ToString() ?? "in this engine"} uses format {format.ToString(CultureInfo.InvariantCulture)}, which this sqlbranch does not read. " +
                "Update the sqlbranch tool and the SqlBranch.Testing package.");
        }

        return format;
    }

    /// <summary>
    /// Whether the name is taken. Format 1 keeps the 0.2.2 rule (the folder exists). On format 2 a name exists when its
    /// catalog has a current version, or when the folder holds a manifest and no catalog (an image an older client wrote,
    /// design D3). Call it under the image lock of the name.
    /// </summary>
    public static async Task<bool> NameExistsAsync(IContainerRuntime runtime, string engineName, string name, int poolFormat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var folder = Quoting.Shell(ImagePaths.Image(name));
        if (poolFormat < 2)
        {
            var plain = await EngineShell.RunAsync(runtime, engineName, $"if [ -e {folder} ]; then echo SQLBRANCH_EXISTS=yes; else echo SQLBRANCH_EXISTS=no; fi", "checking the image name", cancellationToken).ConfigureAwait(false);
            return plain.Contains("SQLBRANCH_EXISTS=yes", StringComparison.Ordinal);
        }

        var catalogPath = Quoting.Shell(ImageCatalogFile.Path(name));
        var manifestPath = Quoting.Shell(ImagePaths.Manifest(name));
        var output = await EngineShell.RunAsync(runtime, engineName,
            $"if [ -f {catalogPath} ]; then echo SQLBRANCH_CATALOGFILE=$(base64 -w0 {catalogPath}); elif [ -f {manifestPath} ]; then echo SQLBRANCH_CATALOGFILE=legacy; else echo SQLBRANCH_CATALOGFILE=none; fi",
            "checking the image name", cancellationToken).ConfigureAwait(false);
        var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))?["SQLBRANCH_CATALOGFILE=".Length..];
        return value switch
        {
            null or "none" => false,
            "legacy" => true,
            _ => (TryParseCatalog(value) ?? throw InvalidCatalog(name)).Current is not null,
        };
    }

    /// <summary>
    /// The manifest of the current version of an image on a format-2 pool, or null when the name does not exist.
    /// Used by <c>branch create</c> (design D3).
    /// </summary>
    public static async Task<ImageManifest?> ReadCurrentManifestAsync(IContainerRuntime runtime, string engineName, string name, CancellationToken cancellationToken) =>
        (await ReadVersionAsync(runtime, engineName, name, null, cancellationToken).ConfigureAwait(false))?.Manifest;

    /// <summary>
    /// One version of an image on a format-2 pool: the current one when <paramref name="versionId"/> is null, otherwise the version of
    /// that ID. Returns null when the name does not exist or its catalog does not list the version. An image an older client wrote
    /// (a manifest and no catalog, D3a) is the current version with a null <see cref="ImageVersionData.Id"/>. The folder and the
    /// manifest come from the same catalog entry.
    /// </summary>
    public static async Task<ImageVersionData?> ReadVersionAsync(IContainerRuntime runtime, string engineName, string name, string? versionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var catalogPath = Quoting.Shell(ImageCatalogFile.Path(name));
        var output = await EngineShell.RunAsync(runtime, engineName,
            $"if [ -f {catalogPath} ]; then echo SQLBRANCH_CATALOGFILE=$(base64 -w0 {catalogPath}); else echo SQLBRANCH_CATALOGFILE=none; fi",
            "reading the image catalog", cancellationToken).ConfigureAwait(false);
        var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("SQLBRANCH_CATALOGFILE=", StringComparison.Ordinal))?["SQLBRANCH_CATALOGFILE=".Length..];
        string directory;
        string? id = null;
        if (value is null or "none")
        {
            if (versionId is not null)
            {
                return null;
            }

            directory = ".";
        }
        else
        {
            var catalog = TryParseCatalog(value)
                ?? throw InvalidCatalog(name);
            id = versionId ?? catalog.Current;
            var entry = id is null ? null : catalog.Versions.FirstOrDefault(v => v.Id == id);
            if (entry is null)
            {
                return null;
            }

            directory = entry.Dir;
        }

        var path = Quoting.Shell($"{ImagePaths.Image(name)}/{directory}/manifest.json");
        var manifestOutput = await EngineShell.RunAsync(runtime, engineName,
            $"if [ -f {path} ]; then echo SQLBRANCH_MANIFEST=$(base64 -w0 {path}); else echo SQLBRANCH_MANIFEST=none; fi",
            "reading the image", cancellationToken).ConfigureAwait(false);
        var text = manifestOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("SQLBRANCH_MANIFEST=", StringComparison.Ordinal))?["SQLBRANCH_MANIFEST=".Length..];
        if (text is null or "none")
        {
            return null;
        }

        var manifest = TryParseManifest(text)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed, $"The manifest of the image '{name}' cannot be read, so no branch can be made from it.");
        return new ImageVersionData(id, directory, manifest);
    }

    internal static SqlBranchException InvalidCatalog(string name) =>
        new(ErrorCodes.OperationFailed, $"The catalog of the image '{name}' is not valid. Run 'sqlbranch image rm {name}' to remove it.");

    private static ImageManifest? TryParseManifest(string base64)
    {
        try
        {
            return ImageManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The image a <c>branch.json</c> names, or null when the file is not readable JSON or has no <c>image</c>.</summary>
    private static string? ImageOfBranch(string base64)
    {
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(base64));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("image", out var image) &&
                image.ValueKind == JsonValueKind.String
                ? image.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }
}

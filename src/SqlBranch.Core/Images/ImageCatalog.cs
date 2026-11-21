using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>One image as <c>image list</c> shows it.</summary>
/// <param name="Name">The image's folder name, which is the name the commands use.</param>
/// <param name="Manifest">The image's manifest.</param>
/// <param name="Branches">The names of the branches created from it, in order.</param>
public sealed record ImageEntry(string Name, ImageManifest Manifest, IReadOnlyList<string> Branches);

/// <summary>What the pool holds: the images, and a note for each folder that looked like one but could not be read.</summary>
public sealed record ImageListing(IReadOnlyList<ImageEntry> Images, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads the images in the pool and counts the branches created from each (design D3: the pool is the state).
/// An image exists once its <c>manifest.json</c> does; <c>.removing-*</c> folders are not images. A branch counts
/// for an image when its <c>branch.json</c> names it in <c>image</c>. Only the engine container reads the pool.
/// </summary>
public sealed class ImageCatalog
{
    private const string ImageTag = "SQLBRANCH_IMAGE";
    private const string BranchTag = "SQLBRANCH_BRANCHJSON";

    /// <summary>
    /// Prints one tagged line per image (name and manifest as base64) and one per branch (name and
    /// <c>branch.json</c> as base64). The glob skips folders that start with a dot, such as <c>.removing-*</c>.
    /// </summary>
    public const string ListingScript =
        "for d in /pool/images/*/; do [ -f \"${d}manifest.json\" ] || continue; " +
        "echo \"SQLBRANCH_IMAGE|$(basename \"$d\")|$(base64 -w0 \"${d}manifest.json\")\"; done; " +
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

    public async Task<ImageListing> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", ListingScript], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"Could not list the images in the pool (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
        }

        var warnings = new List<string>();
        var manifests = new SortedDictionary<string, ImageManifest>(StringComparer.Ordinal);
        var branchImages = new List<(string Branch, string? Image)>();

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            switch (parts[0])
            {
                case ImageTag:
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
                case BranchTag:
                    branchImages.Add((parts[1], ImageOfBranch(parts[2])));
                    break;
                default:
                    break;
            }
        }

        var images = manifests
            .Select(m => new ImageEntry(m.Key, m.Value, [.. branchImages.Where(b => b.Image == m.Key).Select(b => b.Branch).Order(StringComparer.Ordinal)]))
            .ToList();
        return new ImageListing(images, warnings);
    }

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

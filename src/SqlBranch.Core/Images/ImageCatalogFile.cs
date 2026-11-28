using System.Text;
using System.Text.Json;

namespace SqlBranch.Core.Images;

/// <summary>One version in an image's catalog: its ID and the folder of its files, relative to the image folder.</summary>
/// <param name="Id">The version ID, <c>name@yyyyMMddTHHmmssfffZ</c>.</param>
/// <param name="Dir">The folder: <c>.</c> for the legacy files at the top of the image folder, or <c>versions/&lt;id&gt;</c>.</param>
public sealed record ImageVersionRef(string Id, string Dir);

/// <summary>
/// <c>/pool/images/&lt;name&gt;/catalog.json</c> of a format-2 pool (design D3):
/// <c>{"format":1,"name":"shop","current":"shop@...","versions":[{"id":"shop@...","dir":"."}]}</c>.
/// <c>current</c> is null when the name has no version.
/// </summary>
public sealed record ImageCatalogFile(string Name, string? Current, IReadOnlyList<ImageVersionRef> Versions)
{
    public const string FileName = "catalog.json";

    public const string TemporaryName = "catalog.json.new";

    /// <summary>The folder of the current version, or null when the name has none or the catalog does not list it.</summary>
    public string? CurrentDirectory => Current is null ? null : Versions.FirstOrDefault(v => v.Id == Current)?.Dir;

    public static string Path(string name) => $"{ImagePaths.Image(name)}/{FileName}";

    /// <summary>Parses a catalog, or returns null when the text is not a catalog of format 1.</summary>
    public static ImageCatalogFile? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("format").GetInt32() != 1)
            {
                return null;
            }

            var current = root.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var versions = root.GetProperty("versions").EnumerateArray()
                .Select(v => new ImageVersionRef(v.GetProperty("id").GetString()!, v.GetProperty("dir").GetString()!))
                .ToList();
            return new ImageCatalogFile(root.GetProperty("name").GetString()!, current, versions);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            return null;
        }
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            writer.WriteString("name", Name);
            if (Current is null)
            {
                writer.WriteNull("current");
            }
            else
            {
                writer.WriteString("current", Current);
            }

            writer.WriteStartArray("versions");
            foreach (var version in Versions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", version.Id);
                writer.WriteString("dir", version.Dir);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

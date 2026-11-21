using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlBranch.Core.Images;

/// <summary>One database file of an image.</summary>
/// <param name="LogicalName">The file's name inside the database.</param>
/// <param name="FileName">The file's name in the image's <c>data</c> folder.</param>
/// <param name="Type"><c>data</c> or <c>log</c>.</param>
/// <param name="SizeBytes">The file's size.</param>
public sealed record ImageFile(string LogicalName, string FileName, string Type, long SizeBytes)
{
    /// <summary>Fields of this entry that this version does not know, kept as read so an import and a re-export do not lose them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; init; }
}

/// <summary>
/// <c>/pool/images/&lt;name&gt;/manifest.json</c>: what an image is made of and where it came from
/// (design D4, step 7). <c>EngineImage</c> is the exact engine image reference that built it (design D8); manifests written
/// before it was recorded read as an empty string. It is written last, so an image counts as existing only when it is complete.
/// </summary>
public sealed record ImageManifest(
    int Format,
    string Name,
    string SourceDatabase,
    string EngineVersion,
    IReadOnlyList<ImageFile> Files,
    string OriginalRecoveryModel,
    string SealedRecoveryModel,
    long OriginalLogSizeBytes,
    bool LogRebuilt,
    DateTimeOffset BuiltAt,
    string? BackupFile,
    string? BackupSha256,
    IReadOnlyDictionary<string, string> Labels,
    string EngineImage = "")
{
    public string SourceType { get; init; } = "backup";
    public IReadOnlyList<ScriptFile> Scripts { get; init; } = [];
    public IReadOnlyList<ScriptFile> PostScripts { get; init; } = [];
    public string? ServiceBroker { get; init; }

    /// <summary>The backup URL of a URL build, as given (it holds no query string); null for a file or script build and for manifests written before 0.3.</summary>
    public string? BackupUrl { get; init; }
    public string? BacpacFile { get; init; }
    public string? BacpacSha256 { get; init; }
    public string? BacpacUrl { get; init; }

    public IReadOnlyList<string> DdlTriggers { get; init; } = [];

    /// <summary>
    /// Fields this version does not know (for example one a later feature added), kept exactly as read and written back, so an image
    /// that is exported, imported and exported again holds the same manifest except its name.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; init; }

    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The sum of the files' sizes.</summary>
    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ImageManifest FromJson(string json) =>
        JsonSerializer.Deserialize<ImageManifest>(json, Options)
        ?? throw new JsonException("The manifest is empty.");
}

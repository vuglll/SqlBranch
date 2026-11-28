using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlBranch.Core.Branches;

/// <summary>
/// <c>/pool/branches/&lt;name&gt;/branch.json</c>: which image a branch was cloned from and how (design D5).
/// <c>image list</c> and <c>image rm</c> count the branches of an image by <c>image</c>; a reset reads
/// <c>cloneMode</c> to recreate a branch the way it was made.
/// </summary>
/// <param name="Image">The image the files were cloned from.</param>
/// <param name="CloneMode"><c>thin</c> or <c>copy</c>.</param>
/// <param name="CreatedAt">When the branch was created, UTC.</param>
/// <param name="Labels">Free-form branch labels.</param>
/// <param name="State">Null for a healthy branch, <c>broken</c> after a reset that failed once the old database was dropped (design D5).</param>
/// <param name="ExplicitCopy">True when the branch was created with <c>--copy</c>; absent (false) otherwise and in older records. Only such a branch is exempt from <c>requireThin</c> at reset.</param>
/// <param name="Ephemeral">Whether this branch is short-lived.</param>
/// <param name="ExpiresAt">When a short-lived branch becomes eligible for pruning.</param>
/// <param name="ImageVersion">The version ID the branch was cloned from (design D5); null with <paramref name="BaseKnown"/> true means the image was already removed when the pool was upgraded.</param>
/// <param name="BaseKnown">True when the base is recorded, false for an unknown base; null (field absent) for a record no format-2 command has written.</param>
public sealed record BranchRecord(
    string Image,
    string CloneMode,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> Labels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ExplicitCopy = false,
    bool Ephemeral = false,
    DateTimeOffset? ExpiresAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BaseKnown = null)
{
    /// <summary>The value of <see cref="State"/> that marks a branch whose reset failed after the drop.</summary>
    public const string BrokenState = "broken";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson()
    {
        // A known base with no version ("removed before the upgrade") needs an explicit null, which the ignore condition would drop.
        var node = JsonSerializer.SerializeToNode(this, Options)!.AsObject();
        if (BaseKnown is not null && ImageVersion is null)
        {
            node["imageVersion"] = null;
        }

        return node.ToJsonString(Options);
    }

    public static BranchRecord FromJson(string json) =>
        JsonSerializer.Deserialize<BranchRecord>(json, Options)
        ?? throw new JsonException("The branch record is empty.");
}

using SqlBranch.Core.Branches;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Images;

namespace SqlBranch.Testing;

public sealed class SqlBranchOptions
{
    public string Prefix { get; init; } = "t";
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromHours(1);
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public string? ConfigFile { get; init; }

    internal Dictionary<string, string> Validate(string image)
    {
        if (NameRules.Problem(image, false) is { } imageError)
        {
            throw new ArgumentException(imageError, nameof(image));
        }

        if (NameRules.Problem(Prefix, true) is { } prefixError)
        {
            throw new ArgumentException("Invalid branch prefix. " + prefixError);
        }

        if (Prefix.Length > 54)
        {
            throw new ArgumentException("The branch prefix has a 54-character limit.");
        }

        if (TimeLimit <= TimeSpan.Zero || TimeLimit > Duration.Maximum)
        {
            throw new ArgumentException("The time limit must be positive and at most 365 days.");
        }

        ArgumentNullException.ThrowIfNull(Labels);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["sqlbranch.owner"] = "testing" };
        foreach (var (key, value) in Labels)
        {
            if (BranchLabelRules.Problem(key, value) is { } labelError)
            {
                throw new ArgumentException(labelError);
            }

            labels.Add(key, value);
        }
        return labels;
    }
}

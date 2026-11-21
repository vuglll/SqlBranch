using System.Text.RegularExpressions;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

/// <summary>
/// The naming rules shared by images and branches. A name is checked before any SQL
/// runs and before any file is touched, so a name such as <c>../branches/pr-142</c>
/// can never reach a path or a statement.
/// </summary>
public static partial class NameRules
{
    /// <summary>Names SQL Server keeps for itself; a branch is a database, so it may not use them.</summary>
    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    /// <summary>The prefix of SqlBranch's own temporary databases.</summary>
    public const string ReservedPrefix = "sqlbranch_";

    public const int MaxLength = 63;

    /// <summary>The rule as it is stated in messages.</summary>
    public const string RuleText =
        "Names are 1 to 63 characters: lowercase letters, digits, hyphen and underscore, starting with a letter or digit.";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,62}\z")]
    private static partial Regex Pattern();

    /// <summary>Throws exit 2 <c>invalid_name</c> unless <paramref name="name"/> is a valid image name.</summary>
    public static void ValidateImage(string? name) => Validate(name, "image", forBranch: false);

    /// <summary>Throws exit 2 <c>invalid_name</c> unless <paramref name="name"/> is a valid branch name.</summary>
    public static void ValidateBranch(string? name) => Validate(name, "branch", forBranch: true);

    /// <summary>The first problem with the name, or null when it is valid.</summary>
    public static string? Problem(string? name, bool forBranch)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "The name is empty. " + RuleText;
        }

        if (name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            return $"The prefix '{ReservedPrefix}' is reserved for SqlBranch's own temporary databases. " + RuleText;
        }

        if (!Pattern().IsMatch(name))
        {
            return RuleText;
        }

        if (forBranch && SystemDatabases.Contains(name, StringComparer.Ordinal))
        {
            return $"'{name}' is reserved: it is the name of a SQL Server system database. " + RuleText;
        }

        return null;
    }

    private static void Validate(string? name, string kind, bool forBranch)
    {
        if (Problem(name, forBranch) is { } problem)
        {
            throw new SqlBranchException(ErrorCodes.InvalidName, $"Invalid {kind} name '{Printable(name)}'. {problem}");
        }
    }

    /// <summary>The name for a message: control characters become a visible mark, and a very long name is cut.</summary>
    private static string Printable(string? name)
    {
        var text = name ?? string.Empty;
        if (text.Length > 80)
        {
            text = text[..80] + "...";
        }

        return string.Concat(text.Select(c => char.IsControl(c) ? '?' : c));
    }
}

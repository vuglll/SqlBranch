namespace SqlBranch.Core.Branches;

public static class BranchLabelRules
{
    public static string? Problem(string key, string value, bool allowReserved = false)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 63 || !char.IsAsciiLetterOrDigit(key[0]) ||
            key.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-')))
        {
            return "--label keys must start with a lowercase letter or digit and contain 1 to 63 lowercase letters, digits, dots, underscores or hyphens.";
        }

        if (!allowReserved && key.StartsWith("sqlbranch.", StringComparison.Ordinal))
        {
            return "The sqlbranch. prefix is reserved for internal --label keys.";
        }

        if (value is null || value.Length > 256 || value.Any(char.IsControl))
        {
            return "--label values allow up to 256 characters and no control characters.";
        }

        return null;
    }
}

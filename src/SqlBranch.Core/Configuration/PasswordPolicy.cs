namespace SqlBranch.Core.Configuration;

/// <summary>
/// SQL Server's password policy for <c>sa</c>: at least 8 characters, from at
/// least three of uppercase letters, lowercase letters, digits and symbols.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;

    public static bool IsAcceptable(string? password)
    {
        if (password is null || password.Length is < MinimumLength or > MaximumLength)
        {
            return false;
        }

        var classes = 0;
        if (password.Any(char.IsUpper))
        {
            classes++;
        }

        if (password.Any(char.IsLower))
        {
            classes++;
        }

        if (password.Any(char.IsDigit))
        {
            classes++;
        }

        if (password.Any(c => !char.IsLetterOrDigit(c)))
        {
            classes++;
        }

        return classes >= 3;
    }

    /// <summary>Throws a configuration error, without the value, when a given password breaks the policy.</summary>
    public static void CheckGiven(string? password)
    {
        if (password is not null && !IsAcceptable(password))
        {
            throw new ConfigException(
                $"the password does not meet SQL Server's policy: at least {MinimumLength} characters from three of uppercase, lowercase, digits and symbols.",
                SettingKeys.SaPasswordVariable, null);
        }
    }
}

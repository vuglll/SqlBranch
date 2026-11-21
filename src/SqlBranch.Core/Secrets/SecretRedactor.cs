namespace SqlBranch.Core.Secrets;

/// <summary>
/// Replaces registered secrets with <c>***</c>. Every error message, docker
/// command line and engine log excerpt passes through one instance.
/// </summary>
public sealed class SecretRedactor
{
    /// <summary>The text that replaces a secret.</summary>
    public const string Mask = "***";

    private readonly object _gate = new();
    private string[] _secrets = [];

    /// <summary>Adds a secret. Empty values are ignored.</summary>
    public void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(secret, StringComparer.Ordinal))
            {
                // Longest first, so a secret that contains another is masked whole.
                _secrets = [.. _secrets.Append(secret).OrderByDescending(s => s.Length)];
            }
        }
    }

    /// <summary>Returns the text with every registered secret masked.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return result;
    }
}

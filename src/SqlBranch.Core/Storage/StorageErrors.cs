using System.Text;
using System.Text.RegularExpressions;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Storage;

/// <summary>
/// Turns what a storage SDK throws into SqlBranch's own errors (design D5). The message holds the URL without a query and a short
/// reason, and passes through <see cref="SecretRedactor"/> and a scrub of query strings and signature values, so a service that echoes
/// the request URL or a secret in its error never leaks it.
/// </summary>
internal sealed partial class StorageErrors
{
    private const int MaxReason = 240;

    private readonly string _url;
    private readonly SecretRedactor _redactor;

    public StorageErrors(string displayUrl, SecretRedactor redactor)
    {
        _url = displayUrl;
        _redactor = redactor;
    }

    [GeneratedRegex(@"(https?|s3)://[^\s""'<>?#]+[?#][^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlWithQuery();

    [GeneratedRegex(@"(?<k>sig|signature|x-amz-signature|x-amz-credential|x-amz-security-token|sas(?:token)?)=[^&\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureValue();

    /// <summary>Removes registered secrets, every query string of a URL and every signature value from the text.</summary>
    public string Scrub(string? text)
    {
        var result = _redactor.Redact(text);
        result = UrlWithQuery().Replace(result, m => m.Value[..m.Value.IndexOfAny(['?', '#'])]);
        return SignatureValue().Replace(result, "${k}=" + SecretRedactor.Mask);
    }

    /// <summary>The error for a failed storage call: <paramref name="doing"/> reads like "check whether the object exists".</summary>
    public SqlBranchException Unreachable(Exception ex, string doing, int? status = null, string? serviceCode = null)
    {
        var reason = new StringBuilder();
        if (status is > 0 and var code)
        {
            reason.Append("HTTP ").Append(code);
            if (!string.IsNullOrEmpty(serviceCode))
            {
                reason.Append(" (").Append(serviceCode).Append(')');
            }
        }
        else
        {
            reason.Append(Describe(ex));
        }

        return new SqlBranchException(ErrorCodes.StorageUnreachable,
            Scrub($"Could not {doing} at {_url}: {reason}. Check the URL, the credentials in the environment and the server's certificate."));
    }

    /// <summary>A short account of an exception and its inner exceptions, which carry the reason for a TLS or connection failure.</summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null && parts.Count < 3; current = current.InnerException)
        {
            var line = current.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(line) && !parts.Contains(line))
            {
                parts.Add(line.TrimEnd('.'));
            }
        }

        var text = parts.Count == 0 ? ex.GetType().Name : string.Join(": ", parts);
        return text.Length > MaxReason ? text[..MaxReason] + "..." : text;
    }

    public SqlBranchException DestinationExists() =>
        new(ErrorCodes.DestinationExists, Scrub($"{_url} already exists. Use --force to replace it, or choose another destination. It was not changed."));

    public SqlBranchException S3Redirect(string doing, StorageHttp.RefusedRedirectException redirect) =>
        new(ErrorCodes.StorageUnreachable,
            Scrub($"Could not {doing} at {_url}. The S3 endpoint returned a redirect. {StorageHttp.S3RegionGuidance}"), redirect);
}

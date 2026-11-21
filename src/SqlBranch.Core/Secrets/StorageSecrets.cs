using SqlBranch.Core.Images;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Secrets;

/// <summary>
/// The storage secrets of one URL build, read only from the environment. The
/// value is never part of a message, and every form it can take in text is
/// registered with the redactor as soon as it is read.
/// </summary>
public sealed class StorageSecrets
{
    public const string S3KeyIdVariable = "SQLBRANCH_S3_ACCESS_KEY_ID";
    public const string S3SecretVariable = "SQLBRANCH_S3_SECRET_ACCESS_KEY";

    private StorageSecrets(string identity, string secret)
    {
        Identity = identity;
        Secret = secret;
    }

    /// <summary>The SAS token without its leading <c>?</c>, for an <c>https://</c> URL; null for S3.</summary>
    public string? SasToken { get; private init; }

    /// <summary>The S3 access key ID, for an <c>s3://</c> URL; null for Azure.</summary>
    public string? AccessKeyId { get; private init; }

    /// <summary>The S3 secret access key, for an <c>s3://</c> URL; null for Azure.</summary>
    public string? SecretAccessKey { get; private init; }

    /// <summary>The credential's identity: <c>SHARED ACCESS SIGNATURE</c> or <c>S3 Access Key</c>.</summary>
    public string Identity { get; }

    /// <summary>What the credential's SECRET holds: the SAS token, or <c>id:secret</c>.</summary>
    public string Secret { get; }

    /// <summary>
    /// Reads and checks the secrets the URL needs and registers every form with the redactor,
    /// or throws exit 2 <c>config_error</c> naming the variable.
    /// </summary>
    public static StorageSecrets Read(BackupUrl url, IReadOnlyDictionary<string, string> environment, SecretRedactor redactor)
    {
        if (url.Kind == BackupUrlKind.AzureBlob)
        {
            var token = Required(environment, BackupUrl.SasVariable);
            if (token.StartsWith('?'))
            {
                token = token[1..];
            }

            if (token.Length == 0)
            {
                throw Missing(BackupUrl.SasVariable);
            }

            var sig = SigValue(token);
            // The signature is percent-encoded in the token; a service may echo it decoded.
            Register(redactor, token, sig, sig is null ? null : SafeUnescape(sig));
            return new StorageSecrets("SHARED ACCESS SIGNATURE", token) { SasToken = token };
        }

        var id = Required(environment, S3KeyIdVariable);
        var secret = Required(environment, S3SecretVariable);
        // Register both before any refusal, so a refused value cannot reach a message either.
        Register(redactor, id, secret);
        if (id.Contains(':', StringComparison.Ordinal))
        {
            throw Colon(S3KeyIdVariable);
        }

        if (secret.Contains(':', StringComparison.Ordinal))
        {
            throw Colon(S3SecretVariable);
        }

        var joined = $"{id}:{secret}";
        Register(redactor, joined);
        return new StorageSecrets("S3 Access Key", joined) { AccessKeyId = id, SecretAccessKey = secret };
    }

    private static string Required(IReadOnlyDictionary<string, string> environment, string variable) =>
        environment.TryGetValue(variable, out var value) && value.Length > 0 ? value : throw Missing(variable);

    private static SqlBranchException Missing(string variable) =>
        new(ErrorCodes.ConfigError, $"{variable} is not set or is empty. Set it in the environment; storage secrets are never read from flags or config files.");

    private static SqlBranchException Colon(string variable) =>
        new(ErrorCodes.ConfigError, $"{variable} must not contain ':'. The S3 credential joins the key ID and the secret with ':'.");

    private static string? SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? SigValue(string token)
    {
        foreach (var pair in token.Split('&'))
        {
            if (pair.StartsWith("sig=", StringComparison.Ordinal))
            {
                return pair[4..];
            }
        }

        return null;
    }

    /// <summary>Registers each value and the form it takes in a statement, with every <c>'</c> doubled.</summary>
    private static void Register(SecretRedactor redactor, params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            redactor.Register(value);
            redactor.Register(value.Replace("'", "''", StringComparison.Ordinal));

            // A storage service that echoes a request shows the value as it travels in a URL.
            redactor.Register(Uri.EscapeDataString(value));
            redactor.Register(System.Net.WebUtility.UrlEncode(value));
        }
    }
}

using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Tests.Secrets;

public sealed class StorageSecretsTests
{
    private static readonly BackupUrl Blob = BackupUrl.Parse("https://acme.blob.core.windows.net/backups/shop.bak");
    private static readonly BackupUrl S3 = BackupUrl.Parse("s3://s3.amazonaws.com/backups/shop.bak");

    private static Dictionary<string, string> Env(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void A_leading_question_mark_is_dropped_once()
    {
        var secrets = StorageSecrets.Read(Blob, Env(("SQLBRANCH_AZURE_SAS_TOKEN", "?sv=2024-11-04&sp=rl&sig=abc")), new SecretRedactor());
        Assert.Equal("sv=2024-11-04&sp=rl&sig=abc", secrets.Secret);
        Assert.Equal("SHARED ACCESS SIGNATURE", secrets.Identity);

        var twice = StorageSecrets.Read(Blob, Env(("SQLBRANCH_AZURE_SAS_TOKEN", "??sv=1&sig=x")), new SecretRedactor());
        Assert.Equal("?sv=1&sig=x", twice.Secret);
    }

    [Fact]
    public void The_s3_secret_joins_id_and_secret()
    {
        var secrets = StorageSecrets.Read(S3, Env(("SQLBRANCH_S3_ACCESS_KEY_ID", "AKID"), ("SQLBRANCH_S3_SECRET_ACCESS_KEY", "sek")), new SecretRedactor());
        Assert.Equal("AKID:sek", secrets.Secret);
        Assert.Equal("S3 Access Key", secrets.Identity);
    }

    [Fact]
    public void Secret_in_quoted_and_partial_forms_is_masked()
    {
        var redactor = new SecretRedactor();
        StorageSecrets.Read(Blob, Env(("SQLBRANCH_AZURE_SAS_TOKEN", "sv=2024-11-04&sp=rl&sig=Ab'Cd")), redactor);

        var whole = redactor.Redact("The statement was SECRET = N'sv=2024-11-04&sp=rl&sig=Ab''Cd' and failed");
        var partial = redactor.Redact("bad signature Ab''Cd, also Ab'Cd");
        var plain = redactor.Redact("token sv=2024-11-04&sp=rl&sig=Ab'Cd");

        foreach (var text in new[] { whole, partial, plain })
        {
            Assert.Contains("***", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ab''Cd", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ab'Cd", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_s3_form_is_masked()
    {
        var redactor = new SecretRedactor();
        StorageSecrets.Read(S3, Env(("SQLBRANCH_S3_ACCESS_KEY_ID", "AKID'X"), ("SQLBRANCH_S3_SECRET_ACCESS_KEY", "se'k")), redactor);
        foreach (var form in new[] { "AKID'X", "AKID''X", "se'k", "se''k", "AKID'X:se'k", "AKID''X:se''k" })
        {
            Assert.Equal("***", redactor.Redact(form));
        }
    }

    [Fact]
    public void A_refused_colon_value_is_still_registered()
    {
        var redactor = new SecretRedactor();
        var ex = Assert.Throws<SqlBranchException>(() =>
            StorageSecrets.Read(S3, Env(("SQLBRANCH_S3_ACCESS_KEY_ID", "AKID"), ("SQLBRANCH_S3_SECRET_ACCESS_KEY", "wJalr:XUtn")), redactor));
        Assert.Equal(ErrorCodes.ConfigError, ex.Code);
        Assert.Contains("SQLBRANCH_S3_SECRET_ACCESS_KEY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wJalr", ex.Message, StringComparison.Ordinal);
        Assert.Equal("***", redactor.Redact("wJalr:XUtn"));
    }

    [Fact]
    public void A_percent_encoded_signature_is_masked_as_written_and_as_decoded()
    {
        var redactor = new SecretRedactor();
        StorageSecrets.Read(Blob, Env(("SQLBRANCH_AZURE_SAS_TOKEN", "sv=2024-11-04&sp=rl&sig=Zk%2Bq%2Fr7%3D")), redactor);

        var text = redactor.Redact("echo sig=Zk%2Bq%2Fr7%3D, decoded Zk+q/r7=, and again Zk%2Bq%2Fr7%3D");

        Assert.DoesNotContain("Zk%2Bq%2Fr7%3D", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Zk+q/r7=", text, StringComparison.Ordinal);
    }
}

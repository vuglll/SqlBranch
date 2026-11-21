using SqlBranch.Core.Images;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Tests.Images;

public sealed class BackupUrlTests
{
    [Fact]
    public void Blob_url_names_the_container_as_credential()
    {
        var url = BackupUrl.Parse("https://acme.blob.core.windows.net/backups/shop.bak");
        Assert.Equal(BackupUrlKind.AzureBlob, url.Kind);
        Assert.Equal("https://acme.blob.core.windows.net/backups/shop.bak", url.Url);
        Assert.Equal("https://acme.blob.core.windows.net/backups", url.CredentialName);
        Assert.Equal("shop.bak", url.BackupFile);
        Assert.Null(url.S3Region);
    }

    [Fact]
    public void Path_style_s3_url_names_the_folder_as_credential()
    {
        var url = BackupUrl.Parse("s3://172.17.0.5:9000/backups/nightly/shop.bak");
        Assert.Equal(BackupUrlKind.S3, url.Kind);
        Assert.Equal("s3://172.17.0.5:9000/backups/nightly", url.CredentialName);
        Assert.Equal("shop.bak", url.BackupFile);
        Assert.Equal("us-east-1", url.S3Region);
    }

    [Fact]
    public void Virtual_host_s3_url_needs_only_a_file()
    {
        var url = BackupUrl.Parse("s3://acme-db.s3.eu-west-1.amazonaws.com/shop.bak", "eu-west-1");
        Assert.Equal("s3://acme-db.s3.eu-west-1.amazonaws.com", url.CredentialName);
        Assert.Equal("shop.bak", url.BackupFile);
        Assert.Equal("eu-west-1", url.S3Region);
    }

    [Fact]
    public void Schemes_in_upper_case_are_accepted_and_normalized()
    {
        var blob = BackupUrl.Parse("HTTPS://acme.blob.core.windows.net/backups/shop.bak");
        Assert.Equal("https://acme.blob.core.windows.net/backups/shop.bak", blob.Url);
        var s3 = BackupUrl.Parse("S3://s3.amazonaws.com/backups/shop.bak");
        Assert.Equal("s3://s3.amazonaws.com/backups", s3.CredentialName);
        Assert.True(BackupUrl.IsUrl("Http://x"));
    }

    [Fact]
    public void A_percent_encoded_file_name_is_kept_as_given()
    {
        var url = BackupUrl.Parse("https://acme.blob.core.windows.net/backups/my%20shop.bak");
        Assert.Equal("my shop.bak", url.BackupFile);
        Assert.Equal("https://acme.blob.core.windows.net/backups/my%20shop.bak", url.Url);
    }

    [Fact]
    public void A_local_file_named_s3backup_is_not_a_url()
    {
        Assert.False(BackupUrl.IsUrl("s3backup.bak"));
        Assert.False(BackupUrl.IsUrl("./s3://x.bak"));
        Assert.False(BackupUrl.IsUrl(null));
    }

    [Theory]
    [InlineData("http://acme.blob.core.windows.net/backups/shop.bak", "https://")]
    [InlineData("https://acme.blob.core.windows.net/backups/shop.bak?sv=1&sig=AbC123secret", "SQLBRANCH_AZURE_SAS_TOKEN")]
    [InlineData("https://acme.blob.core.windows.net/backups/shop.bak#frag", "query string or fragment")]
    [InlineData("https:///backups/shop.bak", "no host")]
    [InlineData("s3://s3.us-east-1.amazonaws.com/backups/", "backup file")]
    [InlineData("https://acme.blob.core.windows.net/shop.bak", "a container and a file")]
    [InlineData("https://acme.blob.core.windows.net", "a container and a file")]
    [InlineData("s3://s3.amazonaws.com/backups", "a bucket and a file")]
    [InlineData("s3://key:pw@s3.amazonaws.com/backups/shop.bak", "user information")]
    public void Bad_urls_exit_2_with_a_usage_error(string value, string expected)
    {
        var ex = Assert.Throws<SqlBranchException>(() => BackupUrl.Parse(value));
        Assert.Equal(ErrorCodes.UsageError, ex.Code);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AbC123secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_of_260_characters_is_refused_and_259_is_not()
    {
        var prefix = "s3://s3.amazonaws.com/b/";
        var ok = prefix + new string('a', 259 - prefix.Length);
        var long260 = ok + "a";
        Assert.Equal(259, ok.Length);
        Assert.Equal(259, BackupUrl.Parse(ok).Url.Length);
        var ex = Assert.Throws<SqlBranchException>(() => BackupUrl.Parse(long260));
        Assert.Contains("259", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_name_over_128_characters_is_refused()
    {
        var folder = new string('f', 130);
        var value = $"s3://s3.amazonaws.com/{folder}/{new string('a', 40)}.bak";
        Assert.True(value.Length < 260);
        var ex = Assert.Throws<SqlBranchException>(() => BackupUrl.Parse(value));
        Assert.Equal(ErrorCodes.UsageError, ex.Code);
        Assert.Contains("128", ex.Message, StringComparison.Ordinal);

        var edge = "s3://s3.amazonaws.com/" + new string('f', 128 - "s3://s3.amazonaws.com/".Length) + "/x.bak";
        Assert.Equal(128, BackupUrl.Parse(edge).CredentialName.Length);
    }

    [Theory]
    [InlineData("EU West")]
    [InlineData("")]
    [InlineData("eu_west")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_malformed_region_is_refused(string region)
    {
        var ex = Assert.Throws<SqlBranchException>(() => BackupUrl.Parse("s3://s3.amazonaws.com/backups/shop.bak", region));
        Assert.Contains("--s3-region", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_region_without_an_s3_url_is_refused()
    {
        var blob = BackupUrl.Parse("https://acme.blob.core.windows.net/backups/shop.bak");
        Assert.Throws<SqlBranchException>(() => BackupUrl.CheckRegionOption("eu-west-1", blob));
        Assert.Throws<SqlBranchException>(() => BackupUrl.CheckRegionOption("eu-west-1", null));
        BackupUrl.CheckRegionOption(null, null);
    }
}

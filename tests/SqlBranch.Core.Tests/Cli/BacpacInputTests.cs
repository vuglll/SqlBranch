using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Configuration;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class BacpacInputTests
{
    [Theory]
    [InlineData("--from-bak", "shop.bak")]
    [InlineData("--from-scripts", "scripts")]
    [InlineData("--from-bacpac", "second.bacpac")]
    [InlineData("--backup-set", "1")]
    [InlineData("--s3-region", "eu-west-1")]
    [InlineData("--post-script", "missing.sql")]
    [InlineData("--post-script", "unreadable")]
    [InlineData("--sqlpackage", " ")]
    [InlineData("--script-timeout", "0m")]
    [InlineData("--unknown", "private-value")]
    public void Conflicting_options_exit_before_docker(string option, string value)
    {
        var world = NewWorld();
        Refused(world, "usage_error", "shop", "--from-bacpac", "shop.bacpac", option, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing.bacpac")]
    [InlineData("unreadable")]
    [InlineData("folder")]
    [InlineData("bad\0path")]
    [InlineData("http://host/c/x")]
    [InlineData("ftp://host/c/x")]
    [InlineData("s3:///c/x")]
    [InlineData("s3://host/c/")]
    [InlineData("https://host/x")]
    [InlineData("https://user:private-value@host/c/x")]
    [InlineData("https://host/c/x?sig=private-value")]
    [InlineData("s3://host/c/x#private-value")]
    [InlineData("https://host:bad/c/x")]
    public void Malformed_unreadable_or_missing_sources_exit_before_docker(string source)
    {
        Refused(NewWorld(), "usage_error", "shop", "--from-bacpac", source);
    }

    [Fact]
    public void Invalid_name_exits_before_docker() =>
        Refused(NewWorld(), "invalid_name", "Shop DB", "--from-bacpac", "shop.bacpac");

    [Theory]
    [InlineData("s3://host/c/x", "", "key", "us-east-1", "config_error")]
    [InlineData("s3://host/c/x", "example", "private-value:key", "us-east-1", "config_error")]
    [InlineData("s3://host/c/x", "example", "key", "US East", "usage_error")]
    public void Invalid_storage_configuration_exits_before_docker(string source, string key, string secret, string region, string code)
    {
        var world = NewWorld();
        world.Environment[StorageSecrets.S3KeyIdVariable] = key;
        world.Environment[StorageSecrets.S3SecretVariable] = secret;
        Refused(world, code, "shop", "--from-bacpac", source, "--s3-region", region);
    }

    [Theory]
    [InlineData("shop.bacpac")]
    [InlineData("https://acme.blob.core.windows.net/db/shop.bacpac")]
    [InlineData("s3://host/c/x")]
    public void Readable_package_and_valid_urls_get_not_initialized_before_tool_or_content_checks(string source)
    {
        var world = NewWorld();
        world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = "sig=private-value";
        world.Environment[StorageSecrets.S3KeyIdVariable] = "example";
        world.Environment[StorageSecrets.S3SecretVariable] = "private-value";
        // The fake considers this file readable but no path or content exists for opening it.
        var run = world.Run("image", "build", "shop", "--from-bacpac", source,
            "--sqlpackage", "/nonexistent/tool", "--format", "json");
        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", Code(run.Out));
        Assert.Contains("sqlbranch init", run.Out, StringComparison.Ordinal);
        Assert.Empty(world.Connector.Statements);
        Assert.Empty(world.SqlPackageProcess.Calls);
    }

    [Theory]
    [InlineData(false, 3, "not_initialized")]
    [InlineData(true, 2, "usage_error")]
    public void Readable_post_script_is_parsed_only_after_readiness(bool initialized, int exit, string code)
    {
        using var tree = new TempTree();
        var script = tree.File("repeat.sql", "SELECT 1;\nGO 2\n");
        var world = NewWorld();
        if (initialized)
        {
            world.WithEngine();
        }
        world.Files[script] = new FileInfo(script).Length;

        var result = world.Run("image", "build", "shop", "--from-bacpac", "shop.bacpac", "--post-script", script, "--format", "json");

        Assert.Equal(exit, result.Exit);
        Assert.Equal(code, Code(result.Out));
        Assert.Empty(world.SqlPackageProcess.Calls);
    }

    [Fact]
    public void A_real_readable_text_file_gets_not_initialized_and_a_directory_is_refused()
    {
        using var tree = new TempTree();
        tree.File("@notes.txt", "This is not a ZIP.");
        var world = NewWorld();
        var app = CliApp.CreateDefault(world.Redactor, world.Services() with { FileProbe = null, WorkingDirectory = tree.Root });
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(2, app.Run(["image", "build", "shop", "--from-bacpac", tree.Root, "--format", "json"], stdout, stderr));
        Assert.Equal("usage_error", Code(stdout.ToString()));
        Assert.Empty(world.Runtime.Calls);

        stdout.GetStringBuilder().Clear();
        Assert.Equal(3, app.Run(["image", "build", "shop", "--from-bacpac", "@notes.txt", "--format", "json"], stdout, stderr));
        Assert.Equal("not_initialized", Code(stdout.ToString()));
        Assert.Empty(world.SqlPackageProcess.Calls);
    }

    [Fact]
    public void Repeated_source_and_explicit_backup_set_are_refused_in_equals_form()
    {
        Refused(NewWorld(), "usage_error", "shop", "--from-bacpac=shop.bacpac", "--from-bacpac=shop.bacpac");
        Refused(NewWorld(), "usage_error", "shop", "--from-bacpac=shop.bacpac", "--backup-set=1");
    }

    private static CliWorld NewWorld()
    {
        var world = new CliWorld();
        world.Files["/work/shop.bacpac"] = 17;
        world.Files["/work/unreadable"] = 1;
        world.UnreadableFiles.Add("/work/unreadable");
        world.Directories.Add("/work/folder");
        return world;
    }

    private static void Refused(CliWorld world, string code, params string[] arguments)
    {
        var run = world.Run(["image", "build", .. arguments, "--format", "json"]);
        Assert.Equal(2, run.Exit);
        Assert.Equal(code, Code(run.Out));
        Assert.DoesNotContain("private-value", run.Out + run.Err, StringComparison.Ordinal);
        Assert.Empty(world.Runtime.Calls);
        Assert.Empty(world.Connector.Attempts);
        Assert.Empty(world.SqlPackageProcess.Calls);
    }

    private static string? Code(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("code").GetString();
}

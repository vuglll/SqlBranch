using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>The exit-2 checks of <c>image export</c>, which run before any docker command or storage request.</summary>
public sealed class ImageExportChecksTests
{
    private readonly CliWorld _world = new();

    public ImageExportChecksTests()
    {
        _world.WithEngine();
        _world.Directories.Add("/work");
        _world.Runtime.Calls.Clear();
    }

    private (int Exit, string Out, string Err) Export(params string[] args) =>
        _world.Run(["image", "export", .. args, "--format", "json"]);

    private void AssertRefused((int Exit, string Out, string Err) run, string code, params string[] mentions)
    {
        Assert.Equal(2, run.Exit);
        Assert.Equal(code, JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        foreach (var mention in mentions)
        {
            Assert.Contains(mention, run.Out + run.Err, StringComparison.Ordinal);
        }

        Assert.Empty(_world.Runtime.Calls);
        Assert.Empty(_world.Connector.Statements);
        Assert.Empty(_world.Connector.Attempts);
    }

    [Fact]
    public void An_invalid_image_name_exits_2_with_the_naming_rule()
    {
        AssertRefused(Export("Shop DB", "--to", "shop.sbimg"), "invalid_name", "Names are 1 to 63 characters");
    }

    [Fact]
    public void A_missing_destination_exits_2_naming_to()
    {
        AssertRefused(Export("shop"), "usage_error", "--to");
        AssertRefused(Export("shop", "--to", " "), "usage_error", "--to");
    }

    [Fact]
    public void A_destination_that_is_a_folder_exits_2_naming_it()
    {
        _world.Directories.Add("/work/out");

        AssertRefused(Export("shop", "--to", "out"), "usage_error", "'out'");
    }

    [Fact]
    public void A_destination_whose_folder_is_missing_exits_2_naming_the_folder()
    {
        AssertRefused(Export("shop", "--to", "missing/shop.sbimg"), "usage_error", "'missing'");
    }

    [Fact]
    public void A_destination_inside_a_directory_pool_exits_2()
    {
        _world.Environment["SQLBRANCH_POOL"] = "/srv/sqlbranch-pool";
        _world.Directories.Add("/srv/sqlbranch-pool/import");

        AssertRefused(Export("shop", "--to", "/srv/sqlbranch-pool/import/shop.sbimg"), "usage_error", "outside the pool");
        AssertRefused(Export("shop", "--to", "/srv/sqlbranch-pool/shop.sbimg"), "usage_error", "outside the pool");
    }

    [Fact]
    public void A_destination_that_reaches_the_pool_through_a_symbolic_link_exits_2()
    {
        // As /tmp is /private/tmp on macOS: the pool is given by its real path, the destination by the link.
        _world.Environment["SQLBRANCH_POOL"] = "/private/pool";
        _world.Links["/tmp"] = "/private/tmp";
        _world.Links["/shortcut"] = "/private/pool";
        _world.Directories.Add("/shortcut/import");

        AssertRefused(Export("shop", "--to", "/shortcut/import/shop.sbimg"), "usage_error", "outside the pool");

        // and the other way round: the pool is given through the link, the destination by its real path.
        _world.Environment["SQLBRANCH_POOL"] = "/tmp/pool";
        _world.Directories.Add("/private/tmp/pool/import");

        AssertRefused(Export("shop", "--to", "/private/tmp/pool/import/shop.sbimg"), "usage_error", "outside the pool");
    }

    [Fact]
    public void Real_path_resolution_follows_a_real_symbolic_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("sqlbranch-links-").FullName;
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
            File.CreateSymbolicLink(Path.Combine(root, "link"), real);

            Assert.Equal(RealPaths.Resolve(real), RealPaths.Resolve(Path.Combine(root, "link")));
            Assert.Equal(Path.Combine(RealPaths.Resolve(real), "missing", "x.sbimg"), RealPaths.Resolve(Path.Combine(root, "link", "missing", "x.sbimg")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_destination_beside_the_pool_directory_is_not_inside_it()
    {
        _world.Environment["SQLBRANCH_POOL"] = "/srv/sqlbranch-pool";
        _world.Directories.Add("/srv");

        // Not refused by the pool check: it goes on to look for the engine, and its pool is another directory.
        var run = Export("shop", "--to", "/srv/sqlbranch-pool-archives/shop.sbimg");

        Assert.Equal(2, run.Exit);
        Assert.Contains("does not exist", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("outside the pool", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_destination_inside_the_engines_own_pool_directory_exits_2_too()
    {
        // The settings name no pool, but the engine's labels do: caught once the pool is found.
        _world.Environment.Remove("SQLBRANCH_POOL");
        _world.Directories.Add("/data/sb");

        var run = Export("shop", "--to", "/data/sb/shop.sbimg");

        Assert.Equal(2, run.Exit);
        Assert.Contains("outside the pool", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_region_without_an_s3_destination_exits_2()
    {
        AssertRefused(Export("shop", "--to", "shop.sbimg", "--s3-region", "eu-west-1"), "usage_error", "--s3-region");
        AssertRefused(Export("shop", "--to", "https://acme.blob.core.windows.net/images/shop.sbimg", "--s3-region", "eu-west-1"), "usage_error", "--s3-region");
    }

    [Fact]
    public void A_malformed_region_exits_2()
    {
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "AKIDEXAMPLE";
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "secretexample";

        AssertRefused(Export("shop", "--to", "s3://s3.amazonaws.com/images/shop.sbimg", "--s3-region", "EU West"), "usage_error", "--s3-region");
    }

    [Theory]
    [InlineData("http://acme.blob.core.windows.net/images/shop.sbimg", "only https:// and s3://")]
    [InlineData("https://acme.blob.core.windows.net/shop.sbimg", "a container and a file")]
    [InlineData("s3://s3.us-east-1.amazonaws.com/images/", "must name a file")]
    [InlineData("https://acme.blob.core.windows.net/images/", "must name a file")]
    [InlineData("s3://s3.amazonaws.com/images/shop.sbimg#v2", "fragment")]
    [InlineData("s3:///images/shop.sbimg", "no host")]
    [InlineData("HTTP://acme.blob.core.windows.net/images/shop.sbimg", "only https:// and s3://")]
    public void Bad_archive_urls_exit_2_and_run_nothing(string url, string mention)
    {
        var run = Export("shop", "--to", url);

        Assert.Equal(2, run.Exit);
        Assert.Contains(mention, run.Out, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_world.Runtime.Calls);
        Assert.Empty(_world.Connector.Statements);
    }

    [Fact]
    public void A_pasted_sas_url_exits_2_naming_the_variable_without_echoing_the_token()
    {
        var run = Export("shop", "--to", "https://acme.blob.core.windows.net/images/shop.sbimg?sv=2024-11-04&sig=AbC123secret");

        AssertRefused(run, "usage_error", "SQLBRANCH_AZURE_SAS_TOKEN");
        Assert.DoesNotContain("AbC123secret", run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_of_300_characters_is_accepted_for_export_but_not_for_build()
    {
        var url = "s3://s3.amazonaws.com/images/" + new string('a', 300 - 29);
        Assert.Equal(300, url.Length);

        var parsed = BackupUrl.Parse(url, null, "--to", maxUrlLength: null, maxCredentialNameLength: null);
        Assert.Equal(url, parsed.Url);
        Assert.Throws<SqlBranchException>(() => BackupUrl.Parse(url));

        // Through the command: the length is no reason to refuse it, so the next check (the missing secret) answers, with no docker command.
        AssertRefused(Export("shop", "--to", url), "config_error", "SQLBRANCH_S3_ACCESS_KEY_ID");
        Assert.Empty(_world.S3.Requests);
    }

    [Fact]
    public void A_credential_name_over_128_characters_is_accepted_for_export()
    {
        var url = $"s3://s3.amazonaws.com/{new string('f', 130)}/shop.sbimg";

        Assert.Equal(url, BackupUrl.Parse(url, null, "--to", null, null).Url);
        Assert.Throws<SqlBranchException>(() => BackupUrl.Parse(url));
    }

    [Fact]
    public void Valid_input_goes_on_to_look_for_the_engine_which_exits_3_before_init()
    {
        var empty = new CliWorld();
        empty.Directories.Add("/work");

        var run = empty.Run("image", "export", "shop", "--to", "shop.sbimg", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

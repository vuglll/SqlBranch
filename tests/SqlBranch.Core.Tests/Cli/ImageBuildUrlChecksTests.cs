using System.Text.Json;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>The exit-2 checks of a URL build, which run before any docker command or SQL.</summary>
public sealed class ImageBuildUrlChecksTests
{
    private const string Blob = "https://acme.blob.core.windows.net/backups/shop.bak";
    private const string S3 = "s3://172.17.0.5:9000/backups/nightly/shop.bak";

    private readonly CliWorld _world = new();

    public ImageBuildUrlChecksTests()
    {
        _world.WithEngine();
        _world.Runtime.Calls.Clear();
    }

    private (int Exit, string Out, string Err) Build(params string[] extra) =>
        _world.Run(["image", "build", "shop", .. extra, "--format", "json"]);

    private void AssertRefusedBeforeAnyAction((int Exit, string Out, string Err) run, int exit, string code, params string[] mentions)
    {
        Assert.Equal(exit, run.Exit);
        var text = run.Out + run.Err;
        Assert.Equal(code, JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        foreach (var m in mentions)
        {
            Assert.Contains(m, text, StringComparison.Ordinal);
        }

        Assert.Empty(_world.Runtime.Calls);
        Assert.Empty(_world.Connector.Statements);
        Assert.Empty(_world.Connector.Attempts);
    }

    [Theory]
    [InlineData("http://acme.blob.core.windows.net/backups/shop.bak", "https://")]
    [InlineData("https://acme.blob.core.windows.net/shop.bak", "container")]
    [InlineData("https://acme.blob.core.windows.net/backups/", "backup file")]
    [InlineData("https:///backups/shop.bak", "no host")]
    [InlineData("https://acme.blob.core.windows.net/backups/shop.bak#x", "fragment")]
    public void Bad_urls_exit_2_and_run_nothing(string url, string mention)
    {
        AssertRefusedBeforeAnyAction(Build("--from-bak", url), 2, "usage_error", mention);
    }

    [Fact]
    public void A_pasted_sas_url_exits_2_without_echoing_the_token()
    {
        var run = Build("--from-bak", "https://acme.blob.core.windows.net/backups/shop.bak?sv=2024-11-04&sig=AbC123secret");
        AssertRefusedBeforeAnyAction(run, 2, "usage_error", "SQLBRANCH_AZURE_SAS_TOKEN");
        Assert.DoesNotContain("AbC123secret", run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_of_260_characters_exits_2()
    {
        var url = "s3://s3.amazonaws.com/b/" + new string('a', 260 - 24);
        Assert.Equal(260, url.Length);
        AssertRefusedBeforeAnyAction(Build("--from-bak", url), 2, "usage_error", "259");
    }

    [Fact]
    public void A_credential_name_of_130_character_folder_exits_2()
    {
        var url = $"s3://s3.amazonaws.com/{new string('f', 130)}/{new string('a', 40)}.bak";
        AssertRefusedBeforeAnyAction(Build("--from-bak", url), 2, "usage_error", "128");
    }

    [Fact]
    public void A_region_without_an_s3_url_exits_2()
    {
        AssertRefusedBeforeAnyAction(Build("--from-bak", Blob, "--s3-region", "eu-west-1"), 2, "usage_error", "--s3-region");
        AssertRefusedBeforeAnyAction(Build("--from-bak", "shop.bak", "--s3-region", "eu-west-1"), 2, "usage_error", "--s3-region");
    }

    [Fact]
    public void A_malformed_region_exits_2()
    {
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "AKIDEXAMPLE";
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "secretexample";
        AssertRefusedBeforeAnyAction(Build("--from-bak", "s3://s3.amazonaws.com/backups/shop.bak", "--s3-region", "EU West"), 2, "usage_error", "--s3-region");
    }

    [Fact]
    public void Backup_set_2_with_a_url_exits_2()
    {
        AssertRefusedBeforeAnyAction(Build("--from-bak", Blob, "--backup-set", "2"), 2, "usage_error", "--backup-set");
    }

    [Fact]
    public void A_missing_sas_token_exits_2()
    {
        AssertRefusedBeforeAnyAction(Build("--from-bak", Blob), 2, "config_error", "SQLBRANCH_AZURE_SAS_TOKEN");
        _world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = string.Empty;
        AssertRefusedBeforeAnyAction(Build("--from-bak", Blob), 2, "config_error", "SQLBRANCH_AZURE_SAS_TOKEN");
        _world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = "?";
        AssertRefusedBeforeAnyAction(Build("--from-bak", Blob), 2, "config_error", "SQLBRANCH_AZURE_SAS_TOKEN");
    }

    [Fact]
    public void A_missing_s3_secret_exits_2_naming_the_variable()
    {
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "AKIDEXAMPLE";
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = string.Empty;
        AssertRefusedBeforeAnyAction(Build("--from-bak", S3), 2, "config_error", "SQLBRANCH_S3_SECRET_ACCESS_KEY");

        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = string.Empty;
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "secretexample";
        AssertRefusedBeforeAnyAction(Build("--from-bak", S3), 2, "config_error", "SQLBRANCH_S3_ACCESS_KEY_ID");
    }

    [Theory]
    [InlineData("SQLBRANCH_S3_SECRET_ACCESS_KEY", "wJalr:XUtnFEMI")]
    [InlineData("SQLBRANCH_S3_ACCESS_KEY_ID", "AKID:EXAMPLE")]
    public void A_colon_in_an_s3_secret_exits_2_without_showing_it(string variable, string value)
    {
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "AKIDEXAMPLE";
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "secretexample";
        _world.Environment[variable] = value;
        var run = Build("--from-bak", "s3://s3.amazonaws.com/backups/shop.bak");
        AssertRefusedBeforeAnyAction(run, 2, "config_error", variable);
        Assert.DoesNotContain(value, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_secrets_of_the_url_kind_are_read()
    {
        // Blob: only the SAS token is set. The build gets past the checks and creates a SAS credential, never an S3 one.
        _world.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = "?sv=2024-11-04&sp=rl&sig=abc";
        var blob = Build("--from-bak", Blob);
        Assert.NotEqual(2, blob.Exit);
        var blobCreates = _world.Connector.Statements.Where(s => s.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal)).ToList();
        var blobCreate = Assert.Single(blobCreates);
        Assert.Contains("IDENTITY = 'SHARED ACCESS SIGNATURE'", blobCreate, StringComparison.Ordinal);
        Assert.Contains("SECRET = N'sv=2024-11-04&sp=rl&sig=abc'", blobCreate, StringComparison.Ordinal);

        // S3: only the two S3 variables are set. No SAS token is needed, and the credential is an S3 one.
        _world.Connector.Statements.Clear();
        _world.Environment.Remove("SQLBRANCH_AZURE_SAS_TOKEN");
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "AKIDEXAMPLE";
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "secretexample";
        var s3 = Build("--from-bak", S3);
        Assert.NotEqual(2, s3.Exit);
        var s3Create = Assert.Single(_world.Connector.Statements, s => s.StartsWith("CREATE CREDENTIAL", StringComparison.Ordinal));
        Assert.Contains("IDENTITY = 'S3 Access Key'", s3Create, StringComparison.Ordinal);
        Assert.Contains("SECRET = N'AKIDEXAMPLE:secretexample'", s3Create, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_form_of_the_secret_is_registered_before_any_docker_command()
    {
        _world.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = "AKIDEXAMPLE";
        _world.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "se'cretexample";
        var run = Build("--from-bak", S3);
        Assert.NotEmpty(_world.Runtime.Calls);
        Assert.DoesNotContain("se'cretexample", run.Out + run.Err, StringComparison.Ordinal);
        Assert.Equal("***", _world.Redactor.Redact("AKIDEXAMPLE"));
        Assert.Equal("***", _world.Redactor.Redact("se'cretexample"));
        Assert.Equal("***", _world.Redactor.Redact("se''cretexample"));
        Assert.Equal("***", _world.Redactor.Redact("AKIDEXAMPLE:se'cretexample"));
        Assert.Equal("***", _world.Redactor.Redact("AKIDEXAMPLE:se''cretexample"));
    }

    [Theory]
    [InlineData("--sas-token", "SQLBRANCH_AZURE_SAS_TOKEN", false, false)]
    [InlineData("--sas-token", "SQLBRANCH_AZURE_SAS_TOKEN", true, false)]
    [InlineData("--sas-token", "SQLBRANCH_AZURE_SAS_TOKEN", false, true)]
    [InlineData("--s3-access-key-id", "SQLBRANCH_S3_ACCESS_KEY_ID", false, false)]
    [InlineData("--s3-access-key-id", "SQLBRANCH_S3_ACCESS_KEY_ID", true, true)]
    [InlineData("--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY", false, false)]
    [InlineData("--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY", true, false)]
    [InlineData("--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY", false, true)]
    public void A_secret_given_as_a_flag_exits_2_and_is_never_echoed(string flag, string variable, bool equalsForm, bool json)
    {
        const string Secret = "Zz9secretXY";
        List<string> args = ["image", "build", "shop", "--from-bak", Blob];
        if (equalsForm)
        {
            args.Add($"{flag}={Secret}");
        }
        else
        {
            args.AddRange([flag, Secret]);
        }

        if (json)
        {
            args.AddRange(["--format", "json"]);
        }

        var run = _world.Run([.. args]);
        Assert.Equal(2, run.Exit);
        Assert.Contains(variable, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, run.Err, StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Calls);
    }

    [Theory]
    [InlineData("--SAS-TOKEN", false)]
    [InlineData("--s3-secret-key", false)]
    [InlineData("--S3-Secret-Access-Key", false)]
    [InlineData("--s3-secret-key", true)]
    [InlineData("--Sas-Token", true)]
    public void The_token_after_any_unrecognized_option_is_never_echoed(string option, bool equalsForm)
    {
        const string Secret = "Zz9secretXY";
        List<string> args = ["image", "build", "shop", "--from-bak", Blob];
        if (equalsForm)
        {
            args.Add($"{option}={Secret}");
        }
        else
        {
            args.AddRange([option, Secret]);
        }

        foreach (var format in new[] { new[] { "--format", "json" }, Array.Empty<string>() })
        {
            var run = _world.Run([.. args, .. format]);
            Assert.Equal(2, run.Exit);
            Assert.DoesNotContain(Secret, run.Out, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, run.Err, StringComparison.Ordinal);
            Assert.Contains(option.Split('=')[0], run.Out + run.Err, StringComparison.Ordinal);
            Assert.Empty(_world.Runtime.Calls);
        }
    }

    [Theory]
    [InlineData("--bogus", "-Zz9secretXY")]
    [InlineData("--bogus", "--Zz9secretXY")]
    public void A_token_after_an_unknown_option_is_not_echoed_even_when_it_starts_with_a_dash(string option, string value)
    {
        var run = _world.Run("image", "build", "shop", "--from-bak", Blob, option, value);

        Assert.Equal(2, run.Exit);
        Assert.DoesNotContain("Zz9secretXY", run.Out + run.Err, StringComparison.Ordinal);
        Assert.Contains("--bogus", run.Out + run.Err, StringComparison.Ordinal);
        Assert.Empty(_world.Runtime.Calls);
    }

    [Fact]
    public void A_misspelled_command_is_still_named_in_the_parse_error()
    {
        var run = _world.Run("imagee", "list");
        Assert.Equal(2, run.Exit);
        Assert.Contains("imagee", run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_in_a_config_file_exits_2_naming_key_and_path()
    {
        var folder = Directory.CreateTempSubdirectory("sqlbranch-cfg-");
        try
        {
            var file = Path.Combine(folder.FullName, ".sqlbranch.yml");
            File.WriteAllText(file, "s3SecretAccessKey: abc\n");
            var run = _world.Run("image", "list", "--config", file);
            Assert.Equal(2, run.Exit);
            Assert.Contains("s3SecretAccessKey", run.Out + run.Err, StringComparison.Ordinal);
            Assert.Contains(file, run.Out + run.Err, StringComparison.Ordinal);
            Assert.Empty(_world.Runtime.Calls);
        }
        finally
        {
            folder.Delete(true);
        }
    }
}

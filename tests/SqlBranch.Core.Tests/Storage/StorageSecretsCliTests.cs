using System.Text.Json;
using SqlBranch.Core.Tests.Fakes;
using static SqlBranch.Core.Tests.Fakes.StorageScenario;

namespace SqlBranch.Core.Tests.Storage;

/// <summary>"Storage secret sources" and "Storage secrets are checked before any action" for export and import (task 4.1).</summary>
[Collection(StorageSdkSerial.Name)]
public sealed class StorageSecretsCliTests
{
    private readonly StorageScenario _s = new();

    private void AssertRefusedBeforeAnyAction((int Exit, string Out, string Err) run, string code, params string[] mentions)
    {
        Assert.Equal(2, run.Exit);
        Assert.Equal(code, JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        foreach (var mention in mentions)
        {
            Assert.Contains(mention, run.Out + run.Err, StringComparison.Ordinal);
        }

        Assert.Empty(_s.World.Runtime.Calls);
        Assert.Empty(_s.World.Connector.Statements);
        Assert.Empty(_s.World.S3.Requests);
        Assert.Empty(_s.World.Azure.Requests);
        Assert.Empty(_s.Timeline);
    }

    [Fact]
    public void A_missing_sas_token_on_export_exits_2_naming_the_variable_with_no_docker_command_and_no_storage_request()
    {
        AssertRefusedBeforeAnyAction(_s.Export("--to", BlobUrl, "--format", "json"), "config_error", "SQLBRANCH_AZURE_SAS_TOKEN");
    }

    [Fact]
    public void A_sas_token_that_is_only_a_question_mark_is_missing_too()
    {
        _s.World.Environment["SQLBRANCH_AZURE_SAS_TOKEN"] = "?";

        AssertRefusedBeforeAnyAction(_s.Export("--to", BlobUrl, "--format", "json"), "config_error", "SQLBRANCH_AZURE_SAS_TOKEN");
    }

    [Fact]
    public void A_missing_sas_token_on_import_exits_2_before_the_object_is_requested()
    {
        AssertRefusedBeforeAnyAction(_s.Import(BlobUrl, "--format", "json"), "config_error", "SQLBRANCH_AZURE_SAS_TOKEN");
    }

    [Fact]
    public void A_missing_s3_key_on_import_exits_2_naming_the_variable()
    {
        _s.World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = string.Empty;
        _s.World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = Secret;

        var run = _s.Import(S3Url, "--format", "json");

        AssertRefusedBeforeAnyAction(run, "config_error", "SQLBRANCH_S3_ACCESS_KEY_ID");
        Assert.DoesNotContain(Secret, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_import_from_s3_with_no_secret_variables_at_all_exits_2_naming_the_key_id_first()
    {
        AssertRefusedBeforeAnyAction(_s.Import("s3://s3.amazonaws.com/images/shop.sbimg", "--format", "json"), "config_error", "SQLBRANCH_S3_ACCESS_KEY_ID");
    }

    [Fact]
    public void A_missing_s3_secret_on_export_exits_2_naming_the_variable()
    {
        _s.World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = KeyId;

        var run = _s.Export("--to", S3Url, "--format", "json");

        AssertRefusedBeforeAnyAction(run, "config_error", "SQLBRANCH_S3_SECRET_ACCESS_KEY");
        Assert.DoesNotContain(KeyId, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export", "SQLBRANCH_S3_SECRET_ACCESS_KEY", "wJalr:XUtnFEMI")]
    [InlineData("import", "SQLBRANCH_S3_SECRET_ACCESS_KEY", "wJalr:XUtnFEMI")]
    [InlineData("export", "SQLBRANCH_S3_ACCESS_KEY_ID", "AKID:EXAMPLE")]
    [InlineData("import", "SQLBRANCH_S3_ACCESS_KEY_ID", "AKID:EXAMPLE")]
    public void A_colon_in_an_s3_secret_exits_2_naming_the_variable_and_never_echoing_the_value(string command, string variable, string value)
    {
        _s.World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = KeyId;
        _s.World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = Secret;
        _s.World.Environment[variable] = value;

        var run = command == "export" ? _s.Export("--to", S3Url, "--format", "json") : _s.Import(S3Url, "--format", "json");

        AssertRefusedBeforeAnyAction(run, "config_error", variable);
        Assert.DoesNotContain(value, run.Out + run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, run.Out + run.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export", "--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY", false)]
    [InlineData("import", "--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY", false)]
    [InlineData("import", "--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY", true)]
    [InlineData("export", "--sas-token", "SQLBRANCH_AZURE_SAS_TOKEN", false)]
    [InlineData("import", "--sas-token", "SQLBRANCH_AZURE_SAS_TOKEN", true)]
    [InlineData("export", "--s3-access-key-id", "SQLBRANCH_S3_ACCESS_KEY_ID", false)]
    public void A_secret_given_as_a_flag_exits_2_runs_no_docker_command_and_is_never_echoed(string command, string flag, string variable, bool equalsForm)
    {
        const string Given = "Zz9secretXY";
        _s.UseS3Secrets();
        string[] args = command == "export" ? ["--to", S3Url] : [S3Url];
        string[] flagArgs = equalsForm ? [$"{flag}={Given}"] : [flag, Given];

        var run = command == "export" ? _s.Export([.. args, .. flagArgs]) : _s.Import([.. args, .. flagArgs]);

        Assert.Equal(2, run.Exit);
        Assert.DoesNotContain(Given, run.Out + run.Err, StringComparison.Ordinal);
        Assert.Contains(variable, run.Out + run.Err, StringComparison.Ordinal);
        Assert.Empty(_s.World.Runtime.Calls);
        Assert.Empty(_s.World.S3.Requests);
    }

    [Fact]
    public void A_secret_in_a_config_file_exits_2_for_export_and_import_too()
    {
        var folder = Directory.CreateTempSubdirectory("sqlbranch-cfg-");
        try
        {
            var file = Path.Combine(folder.FullName, ".sqlbranch.yml");
            File.WriteAllText(file, "s3SecretAccessKey: abc\n");
            _s.UseS3Secrets();

            var export = _s.World.Run("image", "export", "shop", "--to", S3Url, "--config", file);
            var import = _s.World.Run("image", "import", S3Url, "--config", file);

            foreach (var run in new[] { export, import })
            {
                Assert.Equal(2, run.Exit);
                Assert.Contains("s3SecretAccessKey", run.Out + run.Err, StringComparison.Ordinal);
                Assert.Contains(file, run.Out + run.Err, StringComparison.Ordinal);
            }

            Assert.Empty(_s.World.S3.Requests);
            Assert.Empty(_s.Timeline);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Every_form_of_a_storage_secret_is_registered_with_the_redactor_before_any_request()
    {
        _s.World.Environment["SQLBRANCH_S3_ACCESS_KEY_ID"] = KeyId;
        _s.World.Environment["SQLBRANCH_S3_SECRET_ACCESS_KEY"] = "se/cret+example";
        _s.World.S3.ExpectedAccessKeyId = "someone-else";

        var run = _s.Export("--to", S3Url);

        Assert.Equal(1, run.Exit);
        Assert.Equal("***", _s.World.Redactor.Redact("se/cret+example"));
        Assert.Equal("***", _s.World.Redactor.Redact("se%2Fcret%2Bexample"));
        Assert.Equal("***", _s.World.Redactor.Redact(KeyId));
        Assert.DoesNotContain("se/cret+example", run.Out + run.Err, StringComparison.Ordinal);
    }
}

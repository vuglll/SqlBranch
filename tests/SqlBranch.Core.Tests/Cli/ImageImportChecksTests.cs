using System.Text;
using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Images;
using SqlBranch.Core.Images.Archive;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>
/// The checks of <c>image import</c> that run before any <c>docker</c> command: the exit-2 checks, then the read of the archive's
/// head and its manifest (exit 1 <c>archive_invalid</c>), then the engine (exit 3). Archives are real files in a temporary folder.
/// </summary>
public sealed class ImageImportChecksTests : IDisposable
{
    private readonly CliWorld _world = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("sqlbranch-import-").FullName;
    private readonly byte[] _data = new byte[40_000];
    private readonly byte[] _log = new byte[9_000];

    public ImageImportChecksTests()
    {
        new Random(3).NextBytes(_data);
        new Random(4).NextBytes(_log);
        _world.WithEngine();
        _world.Directories.Add("/work");
        _world.Runtime.Calls.Clear();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ImageManifest Good(string name = "shop") =>
        ArchiveCrafter.Manifest(name, new ImageFile("Shop", "shop.mdf", "data", _data.Length), new ImageFile("Shop_log", "shop_log.ldf", "log", _log.Length));

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Runs the command on the real folder, so the archive is a real file.</summary>
    private (int Exit, JsonElement Json, string Out) RunReal(CliWorld world, params string[] args)
    {
        var services = world.Services() with { WorkingDirectory = _folder, DirectoryExists = Directory.Exists, FileProbe = ClientFile.Probe };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.CreateDefault(world.Redactor, services).Run(["image", "import", .. args, "--format", "json"], stdout, stderr);
        var text = stdout.ToString();
        return (exit, JsonDocument.Parse(text).RootElement, text + stderr);
    }

    private (int Exit, JsonElement Json, string Out) RunReal(params string[] args) => RunReal(_world, args);

    private static string Code(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private void AssertNoDocker()
    {
        Assert.Empty(_world.Runtime.Calls);
        Assert.Empty(_world.Connector.Statements);
        Assert.Empty(_world.Connector.Attempts);
    }

    private (int Exit, string Out, string Err) Fake(params string[] args) => _world.Run(["image", "import", .. args, "--format", "json"]);

    private void AssertRefused((int Exit, string Out, string Err) run, string code, params string[] mentions)
    {
        Assert.Equal(2, run.Exit);
        Assert.Equal(code, JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        foreach (var mention in mentions)
        {
            Assert.Contains(mention, run.Out + run.Err, StringComparison.Ordinal);
        }

        AssertNoDocker();
    }

    // ---- Import input is checked first ----

    [Fact]
    public void A_missing_archive_file_exits_2_naming_it()
    {
        AssertRefused(Fake("nope.sbimg"), "usage_error", "nope.sbimg");
    }

    [Fact]
    public void An_unreadable_archive_file_exits_2_naming_it()
    {
        _world.Files["/work/locked.sbimg"] = 100;
        _world.UnreadableFiles.Add("/work/locked.sbimg");

        AssertRefused(Fake("locked.sbimg"), "usage_error", "locked.sbimg");
    }

    [Fact]
    public void An_archive_path_that_is_a_folder_exits_2_naming_it()
    {
        _world.Directories.Add("/work/out");

        AssertRefused(Fake("out"), "usage_error", "'out'");
    }

    [Fact]
    public void An_invalid_name_in_as_exits_2_with_the_naming_rule()
    {
        _world.Files["/work/shop.sbimg"] = 100;

        AssertRefused(Fake("shop.sbimg", "--as", "../branches/x"), "invalid_name", "Names are 1 to 63 characters");
    }

    [Fact]
    public void A_region_without_an_s3_source_exits_2_naming_it()
    {
        _world.Files["/work/shop.sbimg"] = 100;

        AssertRefused(Fake("shop.sbimg", "--s3-region", "eu-west-1"), "usage_error", "--s3-region");
        AssertRefused(Fake("https://acme.blob.core.windows.net/images/shop.sbimg", "--s3-region", "eu-west-1"), "usage_error", "--s3-region");
    }

    [Theory]
    [InlineData("http://acme.blob.core.windows.net/images/shop.sbimg", "only https:// and s3://")]
    [InlineData("https://acme.blob.core.windows.net/shop.sbimg", "a container and a file")]
    [InlineData("s3://s3.us-east-1.amazonaws.com/images/", "must name a file")]
    [InlineData("s3://s3.amazonaws.com/images/shop.sbimg#v2", "fragment")]
    public void Bad_archive_urls_exit_2_and_run_nothing(string url, string mention)
    {
        var run = Fake(url);

        Assert.Equal(2, run.Exit);
        Assert.Contains(mention, run.Out, StringComparison.OrdinalIgnoreCase);
        AssertNoDocker();
    }

    [Fact]
    public void A_pasted_sas_url_exits_2_without_echoing_the_token()
    {
        var run = Fake("https://acme.blob.core.windows.net/images/shop.sbimg?sv=2024-11-04&sig=AbC123secret");

        AssertRefused(run, "usage_error", "SQLBRANCH_AZURE_SAS_TOKEN");
        Assert.DoesNotContain("AbC123secret", run.Out + run.Err, StringComparison.Ordinal);
    }

    // ---- Import refuses archives it cannot read ----

    [Fact]
    public void A_text_file_is_archive_invalid_and_no_docker_command_ran()
    {
        Write("notes.txt", Encoding.UTF8.GetBytes("These are not the archive you are looking for.\n"));

        var run = RunReal("notes.txt");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void An_invalid_archive_on_a_machine_without_an_engine_is_archive_invalid_not_not_initialized()
    {
        var empty = new CliWorld();
        empty.Directories.Add("/work");
        Write("notes.txt", Encoding.UTF8.GetBytes("plain text"));

        var run = RunReal(empty, "notes.txt");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        Assert.Empty(empty.Runtime.Calls);
        Assert.Empty(empty.Connector.Statements);
    }

    [Fact]
    public async Task A_valid_archive_on_a_machine_without_an_engine_exits_3_not_initialized()
    {
        var empty = new CliWorld();
        empty.Directories.Add("/work");
        Write("shop.sbimg", await ArchiveCrafter.ValidAsync(Good(), _data, _log));

        var run = RunReal(empty, "shop.sbimg");

        Assert.Equal(3, run.Exit);
        Assert.Equal("not_initialized", Code(run.Json));
        Assert.DoesNotContain(empty.Runtime.Calls, c => c.StartsWith("exec", StringComparison.Ordinal) || c.StartsWith("cp", StringComparison.Ordinal));
        Assert.Empty(empty.Connector.Statements);
    }

    [Fact]
    public void A_newer_archive_format_is_archive_invalid_and_says_a_newer_sqlbranch_wrote_it()
    {
        var head = $$"""{ "archiveFormat": 2, "manifest": {{Good().ToJson()}} }""";
        Write("future.sbimg", ArchiveCrafter.Craft(CraftEntry.Text(ImageArchive.HeadEntry, head)));

        var run = RunReal("future.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        Assert.Contains("newer SqlBranch", run.Out, StringComparison.Ordinal);
        AssertNoDocker();
    }

    [Fact]
    public void A_damaged_zstd_header_is_archive_corrupt()
    {
        Write("z.sbimg", [0x28, 0xb5, 0x2f, 0xfd, 0x08, 1, 2, 3, 4]);

        var run = RunReal("z.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_corrupt", Code(run.Json));
        AssertNoDocker();
    }

    // ---- Archive manifests are checked ----

    public static TheoryData<string, string> BadManifests() => new()
    {
        { "traversal", "../branches/pr-1/x.mdf" },
        { "slash", "sub/x.mdf" },
        { "backslash", "sub\\x.mdf" },
        { "nul", "x\0.mdf" },
        { "leading-dot", ".hidden.mdf" },
        { "dotdot-inside", "a..b.mdf" },
        { "empty-name", "" },
    };

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void A_file_name_that_is_not_a_plain_name_is_archive_invalid_before_any_docker_command(string label, string fileName)
    {
        var manifest = Good() with { Files = [new ImageFile("Shop", fileName, "data", 10), new ImageFile("Shop_log", "shop_log.ldf", "log", 10)] };
        Write($"{label}.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(manifest)));

        var run = RunReal($"{label}.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void A_traversal_file_name_in_the_manifest_names_what_is_wrong()
    {
        var manifest = Good() with { Files = [new ImageFile("Shop", "../branches/pr-1/x.mdf", "data", 10)] };
        Write("evil.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(manifest)));

        var run = RunReal("evil.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        Assert.Contains("path separator", run.Out, StringComparison.Ordinal);
        AssertNoDocker();
    }

    [Fact]
    public void An_empty_file_list_is_archive_invalid()
    {
        Write("empty.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(Good() with { Files = [] })));

        var run = RunReal("empty.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void Two_names_that_differ_only_in_case_are_archive_invalid()
    {
        var manifest = Good() with { Files = [new ImageFile("A", "shop.mdf", "data", 10), new ImageFile("B", "SHOP.MDF", "data", 10)] };
        Write("case.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(manifest)));

        var run = RunReal("case.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void A_negative_size_is_archive_invalid()
    {
        var manifest = Good() with { Files = [new ImageFile("Shop", "shop.mdf", "data", -1)] };
        Write("neg.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(manifest)));

        var run = RunReal("neg.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void Sizes_whose_total_overflows_are_archive_invalid()
    {
        var manifest = Good() with { Files = [new ImageFile("A", "a.mdf", "data", long.MaxValue), new ImageFile("B", "b.ndf", "data", 1)] };
        Write("big.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(manifest)));

        var run = RunReal("big.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        Assert.Contains("64-bit", run.Out, StringComparison.Ordinal);
        AssertNoDocker();
    }

    [Fact]
    public void An_unknown_file_type_is_archive_invalid()
    {
        var manifest = Good() with { Files = [new ImageFile("Shop", "shop.mdf", "filestream", 10)] };
        Write("odd.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(manifest)));

        var run = RunReal("odd.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void An_image_name_that_breaks_the_rules_is_archive_invalid_without_as_and_goes_on_with_a_valid_as()
    {
        Write("badname.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(Good("Bad Name"))));

        var refused = RunReal("badname.sbimg");

        Assert.Equal(1, refused.Exit);
        Assert.Equal("archive_invalid", Code(refused.Json));
        Assert.Contains("--as", refused.Out, StringComparison.Ordinal);
        AssertNoDocker();

        // With a valid --as the head is accepted and the command goes on to the engine, which here is a fake that has no image folder.
        var goes = RunReal("badname.sbimg", "--as", "shop2");
        Assert.NotEqual("archive_invalid", goes.Exit == 1 ? Code(goes.Json) : string.Empty);
        Assert.NotEmpty(_world.Runtime.Calls);
    }

    [Fact]
    public void An_engine_version_that_is_not_a_version_is_archive_invalid()
    {
        Write("ver.sbimg", ArchiveCrafter.Craft(ArchiveCrafter.Head(Good() with { EngineVersion = "sixteen" })));

        var run = RunReal("ver.sbimg");

        Assert.Equal(1, run.Exit);
        Assert.Equal("archive_invalid", Code(run.Json));
        AssertNoDocker();
    }

    [Fact]
    public void The_checks_work_on_the_manifest_alone()
    {
        Assert.Equal("shop", ArchiveManifestChecks.Check(Good(), null));
        Assert.Equal("copy", ArchiveManifestChecks.Check(Good(), "copy"));
        Assert.Equal("log", ArchiveManifestChecks.Check(Good() with { Files = [new ImageFile("L", "shop_log.ldf", "log", 0)] }, "log"));
    }
}

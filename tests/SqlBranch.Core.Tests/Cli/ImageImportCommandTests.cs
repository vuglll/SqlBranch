using System.Text.Json;
using SqlBranch.Cli;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>A whole successful <c>image import</c> through the command, on a fake runtime and a real archive file.</summary>
public sealed class ImageImportCommandTests : IDisposable
{
    private readonly CliWorld _world = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("sqlbranch-import-ok-").FullName;
    private readonly byte[] _data = new byte[40_000];
    private readonly byte[] _log = new byte[9_000];

    public ImageImportCommandTests()
    {
        new Random(5).NextBytes(_data);
        new Random(6).NextBytes(_log);
        _world.WithEngine();
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
        {
            var script = command.Count >= 3 ? command[2] : string.Empty;
            if (script.Contains("SQLBRANCH_FREE=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_FREE=" + (100L << 30));
            }

            if (script.Contains("SQLBRANCH_EXISTS=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_EXISTS=no");
            }

            if (script.Contains("stat -c %s", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok($"{_data.Length}\n{_log.Length}");
            }

            if (script.Contains("SQLBRANCH_TAR=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_TAR=ended");
            }

            return script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal) ? inner(container, command) : FakeContainerRuntime.Ok();
        };
        _world.Runtime.StreamHandler = (_, _, _, token) => new FakeStreamingProcess(new CountingSink(), cancellationToken: token);
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

    private async Task WriteArchiveAsync(string engineVersion = "16.0.4135.4")
    {
        var manifest = ArchiveCrafter.Manifest("shop",
            new ImageFile("Shop", "shop.mdf", "data", _data.Length), new ImageFile("Shop_log", "shop_log.ldf", "log", _log.Length)) with { EngineVersion = engineVersion };
        await File.WriteAllBytesAsync(Path.Combine(_folder, "shop.sbimg"), await ArchiveCrafter.ValidAsync(manifest, _data, _log));
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var services = _world.Services() with { WorkingDirectory = _folder, DirectoryExists = Directory.Exists, FileProbe = ClientFile.Probe };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.CreateDefault(_world.Redactor, services).Run(["image", "import", .. args], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("thin")]
    [InlineData("copy")]
    public async Task A_valid_archive_imports_under_a_new_name_and_reports_the_clone_mode(string mode)
    {
        _world.Host.EngineProbe = mode;
        await WriteArchiveAsync();

        var run = Run("shop.sbimg", "--as", "shop2", "--format", "json");

        Assert.Equal(0, run.Exit);
        var json = JsonDocument.Parse(run.Out).RootElement;
        Assert.Equal("image import", json.GetProperty("command").GetString());
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(mode, json.GetProperty("cloneMode").GetString());
        Assert.Equal("shop.sbimg", json.GetProperty("from").GetString());
        var image = json.GetProperty("image");
        Assert.Equal("shop2", image.GetProperty("name").GetString());
        Assert.Equal("Shop", image.GetProperty("sourceDatabase").GetString());
        Assert.Equal(_data.Length + _log.Length, image.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(["shop.mdf", "shop_log.ldf"], image.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("fileName").GetString()));
        Assert.Equal(0, image.GetProperty("branches").GetInt32());
        Assert.Empty(image.GetProperty("branchNames").EnumerateArray());
    }

    [Fact]
    public async Task A_fence_written_between_the_first_read_and_the_image_lock_stops_the_import_before_any_write()
    {
        await WriteArchiveAsync();
        _world.Connector.OnTakeLock = taken =>
        {
            if (taken == EngineLocks.Image("shop2")) { _world.Host.CredentialsJson = Format2Pool.Credentials(UpgradeRecord.Running); }
        };

        var run = Run("shop.sbimg", "--as", "shop2", "--format", "json");

        Assert.Equal(3, run.Exit);
        Assert.Equal("pool_upgrade_required", JsonDocument.Parse(run.Out).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(_world.Connector.TakenLocks, l => l.StartsWith("sqlbranch:build:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Text_output_states_the_clone_mode_and_a_copy_pool_says_branches_copy_every_file()
    {
        _world.Host.EngineProbe = "copy";
        await WriteArchiveAsync();

        var run = Run("shop.sbimg");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Image shop is imported from shop.sbimg.", run.Out, StringComparison.Ordinal);
        Assert.Contains("Clone mode: copy", run.Out, StringComparison.Ordinal);
        Assert.Contains("Branch creation will copy every file in full.", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_build_of_the_same_major_version_succeeds_and_the_warning_goes_to_standard_error()
    {
        _world.Connector.Version = "16.0.4100.1|Developer Edition (64-bit)";
        await WriteArchiveAsync("16.0.4135.4");

        var run = Run("shop.sbimg", "--as", "shop2", "--format", "json");

        Assert.Equal(0, run.Exit);
        Assert.Contains("16.0.4135.4", run.Err, StringComparison.Ordinal);
        Assert.Contains("16.0.4100.1", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_import_whose_cleanup_cannot_finish_says_what_is_left_and_where_to_look()
    {
        await WriteArchiveAsync();
        // The write is cut short as Ctrl+C would, and the engine's tar cannot be confirmed to have ended, so nothing is removed.
        _world.Runtime.StreamHandler = (_, _, _, token) => new FakeStreamingProcess(new CountingSink { CancelOnWrite = true }, cancellationToken: token);
        var inner = _world.Runtime.ExecHandler;
        _world.Runtime.ExecHandler = (container, command) =>
            command.Count >= 3 && command[2].Contains("SQLBRANCH_TAR=", StringComparison.Ordinal) ? FakeContainerRuntime.Ok("SQLBRANCH_TAR=running") : inner(container, command);

        var run = Run("shop.sbimg", "--as", "shop2");

        Assert.Equal(1, run.Exit);
        Assert.Contains("The import was cancelled, but its cleanup could not remove", run.Err, StringComparison.Ordinal);
        Assert.Contains("/pool/build/", run.Err, StringComparison.Ordinal);
        Assert.Contains("'sqlbranch status' lists what is left.", run.Err, StringComparison.Ordinal);
    }
}

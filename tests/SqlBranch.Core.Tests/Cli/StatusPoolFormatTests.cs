using System.Text.Json;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

/// <summary>The pool format in <c>status</c>, and an upgrade that is running or did not finish (task 3.2).</summary>
public sealed class StatusPoolFormatTests
{
    private const string TempCredentials = "/pool/engine/credentials.json.new";

    private readonly CliWorld _world = new();

    public StatusPoolFormatTests()
    {
        _world.WithEngine();
    }

    private (int Exit, JsonElement Json, string Out) Status()
    {
        var run = _world.Run("status", "--format", "json");
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out);
    }

    private static string[] Kinds(JsonElement json) =>
        [.. json.GetProperty("mismatches").EnumerateArray().Select(m => m.GetProperty("kind").GetString()!)];

    private static string[] Messages(JsonElement json) =>
        [.. json.GetProperty("mismatches").EnumerateArray().Select(m => m.GetProperty("message").GetString()!)];

    [Fact]
    public void Status_of_a_new_pool_reports_pool_format_1_in_json_and_text()
    {
        var run = Status();

        Assert.Equal(0, run.Exit);
        Assert.Equal(1, run.Json.GetProperty("poolFormat").GetInt32());
        Assert.Contains("Pool format: 1", _world.Run("status").Out, StringComparison.Ordinal);
        Assert.Empty(_world.Connector.ProbedLocks);
    }

    [Fact]
    public void A_helper_written_format_2_pool_reports_pool_format_2_and_exits_0()
    {
        Format2Pool.Apply(_world);

        var run = Status();

        Assert.Equal(0, run.Exit);
        Assert.Equal(2, run.Json.GetProperty("poolFormat").GetInt32());
        Assert.Contains("Pool format: 2", _world.Run("status").Out, StringComparison.Ordinal);
        Assert.Empty(Kinds(run.Json));
    }

    [Fact]
    public void An_interrupted_upgrade_with_the_lock_free_is_a_mismatch_that_names_the_rerun()
    {
        Format2Pool.Apply(_world, UpgradeRecord.Running);

        var run = Status();

        Assert.Equal(1, run.Exit);
        Assert.Equal(["pool_upgrade"], Kinds(run.Json));
        var message = Messages(run.Json).Single();
        Assert.Contains("did not finish", message, StringComparison.Ordinal);
        Assert.Contains("refused until it finishes", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", _world.Run("status").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_temporary_credentials_file_with_the_lock_free_is_an_upgrade_leftover_shown_by_name_only()
    {
        _world.Host.UpgradeTemporaryFiles.Add(TempCredentials);

        var run = Status();

        Assert.Equal(1, run.Exit);
        Assert.Equal(["upgrade_leftover"], Kinds(run.Json));
        var message = Messages(run.Json).Single();
        Assert.Contains(TempCredentials, message, StringComparison.Ordinal);
        Assert.Contains("sqlbranch pool upgrade --yes", message, StringComparison.Ordinal);
        Assert.DoesNotContain(CliWorld.StoredPassword, run.Out, StringComparison.Ordinal);
        Assert.Equal(1, run.Json.GetProperty("poolFormat").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_writer_temporary_files_are_not_upgrade_leftovers_on_a_format_1_or_a_finished_format_2_pool(bool finished)
    {
        if (finished) { Format2Pool.Apply(_world, UpgradeRecord.Done); }
        _world.Host.UpgradeTemporaryFiles.AddRange(
            ["/pool/branches/dev1/branch.json.new", "/pool/images/shop/catalog.json.new", "/pool/images/.ids/shop.new"]);

        var run = Status();

        Assert.Equal(0, run.Exit);
        Assert.Empty(Kinds(run.Json));
        Assert.Empty(run.Json.GetProperty("inProgress").EnumerateArray());
    }

    [Fact]
    public void While_the_fence_is_running_and_the_lock_is_free_every_listed_temporary_file_is_a_leftover_by_full_path()
    {
        Format2Pool.Apply(_world, UpgradeRecord.Running);
        _world.Host.UpgradeTemporaryFiles.AddRange([TempCredentials, "/pool/branches/dev1/branch.json.new"]);

        var run = Status();

        Assert.Equal(1, run.Exit);
        Assert.Equal(["pool_upgrade", "upgrade_leftover", "upgrade_leftover"], Kinds(run.Json));
        Assert.Contains("/pool/branches/dev1/branch.json.new", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_upgrade_is_in_progress_and_exits_0_also_with_its_temporary_file()
    {
        Format2Pool.Apply(_world, UpgradeRecord.Running);
        _world.Host.UpgradeTemporaryFiles.Add(TempCredentials);
        _world.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade);

        var run = Status();

        Assert.Equal(0, run.Exit);
        Assert.Empty(Kinds(run.Json));
        Assert.Contains("pool upgrade is running", run.Json.GetProperty("inProgress").EnumerateArray().Select(e => e.GetString()), StringComparer.Ordinal);
        Assert.Contains("In progress: pool upgrade is running", _world.Run("status").Out, StringComparison.Ordinal);
        var temp = $"pool upgrade temporary file {TempCredentials}";
        Assert.Contains(temp, run.Json.GetProperty("inProgress").EnumerateArray().Select(e => e.GetString()), StringComparer.Ordinal);
        Assert.Contains(temp, _world.Run("status").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_upgrade_that_only_wrote_the_temporary_file_is_not_a_leftover()
    {
        _world.Host.UpgradeTemporaryFiles.Add(TempCredentials);
        _world.Connector.HeldLocks.Add(EngineLocks.PoolUpgrade);

        var run = Status();

        Assert.Equal(0, run.Exit);
        Assert.Empty(Kinds(run.Json));
    }

    [Fact]
    public void Status_tests_the_upgrade_lock_and_never_takes_it()
    {
        Format2Pool.Apply(_world, UpgradeRecord.Running);
        _world.Connector.TrackSessionLocks = true;

        Status();
        _world.Host.UpgradeTemporaryFiles.Add(TempCredentials);
        Status();

        Assert.Contains(EngineLocks.PoolUpgrade, _world.Connector.ProbedLocks);
        Assert.DoesNotContain(EngineLocks.PoolUpgrade, _world.Connector.TakenLocks);
        Assert.DoesNotContain(EngineLocks.PoolUpgrade, _world.Connector.ReleasedLocks);
        Assert.DoesNotContain(_world.Connector.Statements, s => s.Contains("sp_getapplock", StringComparison.Ordinal) && s.Contains("pool-upgrade", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pool_from_a_newer_release_is_refused_by_every_command_that_reads_the_credentials()
    {
        _world.Host.CredentialsJson = """{"format":3,"engine":{"user":"sa","password":"Stored#Pass1"}}""";

        var run = _world.Run("branch", "list", "--format", "json");
        var json = JsonDocument.Parse(run.Out).RootElement;

        Assert.Equal(1, run.Exit);
        Assert.Equal("operation_failed", json.GetProperty("error").GetProperty("code").GetString());
        var message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("format 3", message, StringComparison.Ordinal);
        Assert.Contains("Update the sqlbranch tool and the SqlBranch.Testing package", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Stored#Pass1", run.Out + run.Err, StringComparison.Ordinal);
    }
}

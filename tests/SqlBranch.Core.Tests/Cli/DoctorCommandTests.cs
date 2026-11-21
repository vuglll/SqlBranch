using System.Text.Json;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Output;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Cli;

public sealed class DoctorCommandTests
{
    private readonly CliWorld _world = new();

    /// <summary>A machine with a working engine on the plain pool, as init leaves it.</summary>
    private void Initialized()
    {
        _world.WithEngine();
        _world.Directories.Add(FakeInitHost.PoolDir);
        _world.Host.DirectoryReflink = true;
    }

    private (int Exit, JsonElement Json, string Out, string Err) Run(params string[] args)
    {
        var run = _world.Run(["doctor", .. args, "--format", "json"]);
        return (run.Exit, JsonDocument.Parse(run.Out).RootElement, run.Out, run.Err);
    }

    private static string StatusOf(JsonElement root, string id) =>
        root.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("status").GetString()!;

    private static string DetailOf(JsonElement root, string id) =>
        root.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("detail").GetString()!;

    [Fact]
    public void Explicit_sqlpackage_path_overrides_PATH_and_reports_its_version()
    {
        var path = Path.Combine(Path.GetTempPath(), "other sqlpackage");
        var text = _world.Run("doctor", "--sqlpackage", path);
        var json = _world.Run("doctor", "--sqlpackage", path, "--format", "json");

        Assert.Equal(0, text.Exit);
        Assert.Equal(0, json.Exit);
        Assert.Contains(path, text.Out, StringComparison.Ordinal);
        Assert.Contains("170.5.96.0", DetailOf(JsonDocument.Parse(json.Out).RootElement, "sqlpackage"), StringComparison.Ordinal);
        Assert.All(_world.SqlPackageProcess.Calls, call =>
        {
            Assert.Equal(path, call.File);
            Assert.Equal(["/Version"], call.Args);
        });
    }

    [Fact]
    public void PATH_skips_an_empty_quoted_entry_and_uses_the_first_launchable_executable()
    {
        var unavailable = Path.Combine(Path.GetTempPath(), "missing installation");
        var available = Path.Combine(Path.GetTempPath(), "SqlPackage installation");
        _world.Environment["PATH"] = string.Join(Path.PathSeparator, "\"\"", unavailable, $"\"{available}\"");
        _world.SqlPackageProcess.Handler = _ => _world.SqlPackageProcess.Calls.Count == 1
            ? throw new System.ComponentModel.Win32Exception()
            : new ProcessOutput(0, "170.5.96.0", "");

        var run = Run();

        var file = OperatingSystem.IsWindows() ? "sqlpackage.exe" : "sqlpackage";
        Assert.Equal(0, run.Exit);
        Assert.Equal([Path.Combine(unavailable, file), Path.Combine(available, file)],
            _world.SqlPackageProcess.Calls.Select(call => call.File));
        Assert.Equal($"{Path.Combine(available, file)} (version 170.5.96.0)", DetailOf(run.Json, "sqlpackage"));
    }

    [Theory]
    [InlineData(1, "170.5.96.0")]
    [InlineData(0, "unexpected output")]
    public void PATH_skips_an_executable_with_a_failed_version_probe(int exitCode, string versionOutput)
    {
        var first = Path.Combine(Path.GetTempPath(), "broken installation");
        var second = Path.Combine(Path.GetTempPath(), "working installation");
        _world.Environment["PATH"] = string.Join(Path.PathSeparator, first, second);
        _world.SqlPackageProcess.Handler = _ => _world.SqlPackageProcess.Calls.Count == 1
            ? new ProcessOutput(exitCode, versionOutput, "private tool output")
            : new ProcessOutput(0, "170.5.96.0", "");

        var run = Run();

        var file = OperatingSystem.IsWindows() ? "sqlpackage.exe" : "sqlpackage";
        Assert.Equal(0, run.Exit);
        Assert.Equal([Path.Combine(first, file), Path.Combine(second, file)],
            _world.SqlPackageProcess.Calls.Select(call => call.File));
        Assert.Equal($"{Path.Combine(second, file)} (version 170.5.96.0)", DetailOf(run.Json, "sqlpackage"));
    }

    [Fact]
    public void PATH_continues_after_a_timeout_with_a_fresh_deadline()
    {
        _world.Environment["PATH"] = string.Join(Path.PathSeparator,
            Path.Combine(Path.GetTempPath(), "slow installation"), Path.GetTempPath());
        _world.SqlPackageProcess.Handler = _ => _world.SqlPackageProcess.Calls.Count == 1
            ? throw new OperationCanceledException(_world.SqlPackageProcess.CancellationTokens[0])
            : new ProcessOutput(0, "170.5.96.0", "");

        var run = Run();

        Assert.Equal(0, run.Exit);
        var tokens = _world.SqlPackageProcess.CancellationTokens;
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.True(token.CanBeCanceled));
        Assert.NotEqual(tokens[0], tokens[1]);
    }

    [Fact]
    public void Version_probe_receives_only_the_shim_environment()
    {
        var allowed = new Dictionary<string, string>
        {
            ["PATH"] = _world.Environment["PATH"],
            ["HOME"] = Path.Combine(Path.GetTempPath(), "probe home"),
            ["TMPDIR"] = Path.GetTempPath(),
            ["DOTNET_ROOT"] = "runtime",
            ["DOTNET_ROOT_X64"] = "x64 runtime",
            ["DOTNET_ROOT_ARM64"] = "arm64 runtime",
        };
        foreach (var (name, value) in allowed)
        {
            _world.Environment[name] = value;
        }
        _world.Environment["DOTNET_ROOTED"] = "excluded";
        _world.Environment["SQLBRANCH_SA_PASSWORD"] = CliWorld.StoredPassword;

        var run = Run();

        Assert.Equal(0, run.Exit);
        var call = Assert.Single(_world.SqlPackageProcess.Calls);
        Assert.NotNull(call.Env);
        Assert.Equal(allowed.OrderBy(entry => entry.Key), call.Env.OrderBy(entry => entry.Key));
    }

    [Fact]
    public void Missing_explicit_sqlpackage_does_not_fall_back_to_PATH()
    {
        _world.SqlPackageProcess.Handler = _ => _world.SqlPackageProcess.Calls.Count == 1
            ? throw new System.ComponentModel.Win32Exception()
            : new ProcessOutput(0, "170.5.96.0", "");
        var missing = Path.Combine(Path.GetTempPath(), "missing-sqlpackage");

        var run = Run("--sqlpackage", missing);

        Assert.Equal(3, run.Exit);
        Assert.Equal("sqlpackage_missing", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fail", StatusOf(run.Json, "sqlpackage"));
        Assert.Contains(missing, run.Json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(missing, DetailOf(run.Json, "sqlpackage"), StringComparison.Ordinal);
        Assert.Equal(missing, Assert.Single(_world.SqlPackageProcess.Calls).File);
    }

    [Theory]
    [InlineData(1, "170.5.96.0")]
    [InlineData(0, "unexpected output")]
    public void Explicit_sqlpackage_with_a_failed_version_probe_does_not_search_PATH(int exitCode, string versionOutput)
    {
        _world.SqlPackageProcess.Handler = _ => _world.SqlPackageProcess.Calls.Count == 1
            ? new ProcessOutput(exitCode, versionOutput, "private tool output")
            : new ProcessOutput(0, "170.5.96.0", "");
        var path = Path.Combine(Path.GetTempPath(), "broken sqlpackage");

        var run = Run("--sqlpackage", path);

        Assert.Equal(3, run.Exit);
        Assert.Equal("sqlpackage_missing", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(path, DetailOf(run.Json, "sqlpackage"), StringComparison.Ordinal);
        Assert.DoesNotContain("private tool output", run.Out + run.Err, StringComparison.Ordinal);
        Assert.Equal(path, Assert.Single(_world.SqlPackageProcess.Calls).File);
    }

    [Fact]
    public void Missing_sqlpackage_is_optional_in_json_and_text()
    {
        Initialized();
        _world.Host.Space = (100_000_000_000, 10_000_000_000, 90_000_000_000);
        _world.Runtime.Info = new RuntimeInfo("Ubuntu 24.04", "x86_64", "host");
        _world.SqlPackageProcess.Throw = new System.ComponentModel.Win32Exception();

        var run = Run();
        var text = _world.Run("doctor");

        Assert.Equal(0, run.Exit);
        Assert.True(run.Json.GetProperty("ok").GetBoolean());
        Assert.Equal("optional", StatusOf(run.Json, "sqlpackage"));
        var detail = DetailOf(run.Json, "sqlpackage");
        Assert.Contains("Not installed. Needed only for --from-bacpac.", detail, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install -g microsoft.sqlpackage", detail, StringComparison.Ordinal);
        Assert.Equal(0, text.Exit);
        Assert.Contains($"[optional] SqlPackage: {detail}", text.Out, StringComparison.Ordinal);
        Assert.Contains("All required checks passed.", text.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "docker_unavailable", "optional")]
    [InlineData(true, "sqlpackage_missing", "fail")]
    public void Only_an_explicit_sqlpackage_failure_takes_precedence_over_docker_failure(
        bool explicitPath, string errorCode, string status)
    {
        _world.Runtime.InfoFailure = new SqlBranchException(ErrorCodes.DockerUnavailable, "Docker is unavailable.");
        _world.SqlPackageProcess.Throw = new System.ComponentModel.Win32Exception();

        var run = explicitPath ? Run("--sqlpackage", Path.Combine(Path.GetTempPath(), "missing sqlpackage")) : Run();

        Assert.Equal(3, run.Exit);
        Assert.Equal(errorCode, run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fail", StatusOf(run.Json, "docker"));
        Assert.Equal(status, StatusOf(run.Json, "sqlpackage"));
    }

    [Fact]
    public void Probe_does_not_report_unrecognized_version_output()
    {
        _world.SqlPackageProcess.Handler = _ => new ProcessOutput(0, "unexpected diagnostic", "private tool output");

        var run = Run();

        Assert.Equal(0, run.Exit);
        Assert.Equal("optional", StatusOf(run.Json, "sqlpackage"));
        Assert.DoesNotContain("unexpected diagnostic", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("private tool output", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_volume_without_the_managed_label_is_reported_and_nothing_is_written_into_it()
    {
        _world.WithEngine(true, "plain", ContainerLabels.PoolVolume);
        _world.Runtime.Volumes.Add(ContainerLabels.PoolVolume);
        _world.Runtime.UnlabeledVolumes.Add(ContainerLabels.PoolVolume);

        var run = Run();

        Assert.Equal("fail", StatusOf(run.Json, "pool"));
        Assert.Contains("was not created by SqlBranch", DetailOf(run.Json, "pool"), StringComparison.Ordinal);
        Assert.DoesNotContain(_world.Runtime.Runs, r => r.Mounts.Any(m => m.Source == ContainerLabels.PoolVolume));
    }

    [Fact]
    public void All_checks_pass_on_a_healthy_initialized_machine()
    {
        Initialized();
        _world.Host.Space = (100_000_000_000, 10_000_000_000, 90_000_000_000);
        _world.Runtime.Info = new RuntimeInfo("Ubuntu 24.04", "x86_64", "host");

        var text = _world.Run("doctor");
        var run = Run();

        Assert.Equal(0, text.Exit);
        Assert.Contains("[pass] Docker is reachable", text.Out, StringComparison.Ordinal);
        Assert.Contains("All checks passed.", text.Out, StringComparison.Ordinal);
        Assert.Equal(0, run.Exit);
        foreach (var id in new[] { "docker", "image", "pool", "port", "space", "host", "cloneMode" })
        {
            Assert.Equal("pass", StatusOf(run.Json, id));
        }
    }

    [Fact]
    public void Doctor_works_before_init_without_sqlpackage_and_creates_nothing()
    {
        // Nothing exists: no engine, no pool file, no volume, no directory.
        _world.Environment.Remove("PATH");
        var text = _world.Run("doctor");
        var run = Run();

        Assert.Equal(0, text.Exit);
        Assert.Equal(0, run.Exit);
        Assert.Equal("optional", StatusOf(run.Json, "sqlpackage"));
        Assert.Equal("pass", StatusOf(run.Json, "docker"));
        Assert.Equal("pass", StatusOf(run.Json, "pool"));
        Assert.Equal("pass", StatusOf(run.Json, "port"));
        Assert.DoesNotContain(_world.Runtime.Calls, c => c.StartsWith("volume create", StringComparison.Ordinal));
        Assert.False(_world.Host.PoolFileExists);
        Assert.False(_world.Host.Mounted);
        Assert.Empty(_world.Runtime.Containers);
    }

    [Fact]
    public void Docker_not_reachable_fails_with_docker_unavailable_and_exit_3()
    {
        _world.Runtime.InfoFailure = new SqlBranchException(ErrorCodes.DockerUnavailable, "The 'docker' command was not found.");

        var run = Run();

        Assert.Equal(3, run.Exit);
        Assert.Equal("docker_unavailable", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fail", StatusOf(run.Json, "docker"));
    }

    [Fact]
    public void A_port_held_by_another_process_fails_with_the_port_number()
    {
        _world.PortFree = false;

        var text = _world.Run("doctor");
        var run = Run();

        Assert.Equal(3, text.Exit);
        Assert.Contains("[fail]", text.Out, StringComparison.Ordinal);
        Assert.Equal(3, run.Exit);
        Assert.Equal("check_failed", run.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fail", StatusOf(run.Json, "port"));
        Assert.Contains("14330", DetailOf(run.Json, "port"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_held_by_the_running_engine_passes_even_when_it_is_bound()
    {
        Initialized();
        _world.PortFree = false;

        var run = Run();

        Assert.Equal(0, run.Exit);
        Assert.Equal("pass", StatusOf(run.Json, "port"));
        Assert.Contains("held by sqlbranch-engine", DetailOf(run.Json, "port"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_engine_whose_port_is_taken_fails()
    {
        _world.WithEngine(running: false);
        _world.Directories.Add(FakeInitHost.PoolDir);
        _world.PortFree = false;

        var run = Run();

        Assert.Equal(3, run.Exit);
        Assert.Equal("fail", StatusOf(run.Json, "port"));
    }

    [Theory]
    [InlineData(6L << 30, "warn", 0)]
    [InlineData(10L << 30, "pass", 0)]
    [InlineData((10L << 30) - 1, "warn", 0)]
    [InlineData(1L << 30, "warn", 0)]
    [InlineData((1L << 30) - 1, "fail", 3)]
    [InlineData(500L << 20, "fail", 3)]
    public void Free_pool_space_warns_under_10_GiB_and_fails_under_1_GiB(long free, string status, int exit)
    {
        Initialized();
        _world.Host.Space = (100_000_000_000, 100_000_000_000 - free, free);

        var run = Run();

        Assert.Equal(status, StatusOf(run.Json, "space"));
        Assert.Equal(exit, run.Exit);
    }

    [Fact]
    public void Six_GiB_free_warns_and_exits_0_while_everything_else_passes()
    {
        Initialized();
        _world.Host.Space = (100_000_000_000, 0, 6L << 30);

        var run = Run();

        Assert.Equal(0, run.Exit);
        Assert.Equal("warn", StatusOf(run.Json, "space"));
        Assert.Contains("6 GiB", DetailOf(run.Json, "space"), StringComparison.Ordinal);
        Assert.True(run.Json.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void An_arm64_host_warns_that_the_engine_runs_under_emulation_and_exits_0()
    {
        Initialized();
        _world.Runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");

        var text = _world.Run("doctor");
        var run = Run();

        Assert.Equal(0, text.Exit);
        Assert.Equal(0, run.Exit);
        Assert.Equal("warn", StatusOf(run.Json, "host"));
        Assert.Contains("emulation", DetailOf(run.Json, "host"), StringComparison.Ordinal);
        Assert.Contains("[warn] Host is x86-64 Linux", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_mode_warns_and_exits_0()
    {
        Initialized();
        // The doctor probes from a work container, which finds no block sharing here.
        _world.Host.DirectoryReflink = false;

        var run = Run();

        Assert.Equal(0, run.Exit);
        Assert.Equal("warn", StatusOf(run.Json, "cloneMode"));
        Assert.Contains("copy every file in full", DetailOf(run.Json, "cloneMode"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_engine_image_that_is_not_downloaded_warns()
    {
        Initialized();
        _world.Runtime.Images.Add("some-other-image");

        var run = Run();

        Assert.Equal(0, run.Exit);
        Assert.Equal("warn", StatusOf(run.Json, "image"));
        Assert.Contains("init", DetailOf(run.Json, "image"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_that_cannot_be_written_fails()
    {
        Initialized();
        _world.Host.PoolWritable = false;

        var run = Run();

        Assert.Equal(3, run.Exit);
        Assert.Equal("fail", StatusOf(run.Json, "pool"));
        Assert.Equal("check_failed", run.Json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void An_unmounted_managed_pool_warns_and_doctor_does_not_mount_it()
    {
        _world.WithEngine(running: false, backend: "loop-xfs", pool: ManagedPaths.Default.MountPoint);
        _world.Host.PoolFileExists = true;
        _world.Host.Mounted = false;

        var run = Run();

        Assert.Equal(0, run.Exit);
        Assert.Equal("warn", StatusOf(run.Json, "pool"));
        Assert.False(_world.Host.Mounted);
    }

    [Fact]
    public void A_foreign_container_with_the_engines_name_fails_the_pool_check_and_is_untouched()
    {
        _world.Runtime.Containers[ContainerLabels.EngineName] =
            new ContainerInfo(ContainerLabels.EngineName, "postgres:16", true, new Dictionary<string, string>(), []);

        var run = Run();

        Assert.Equal(3, run.Exit);
        Assert.Equal("fail", StatusOf(run.Json, "pool"));
        Assert.True(_world.Runtime.Containers[ContainerLabels.EngineName].Running);
    }

    [Fact]
    public void The_password_is_absent_from_the_output()
    {
        Initialized();
        _world.PortFree = false;
        var outputs = new List<string>();
        foreach (var args in new[] { new[] { "doctor" }, ["doctor", "--format", "json"] })
        {
            var run = _world.Run(args);
            outputs.AddRange([run.Out, run.Err]);
        }

        Assert.All(outputs, o => Assert.DoesNotContain(CliWorld.StoredPassword, o, StringComparison.Ordinal));
        Assert.DoesNotContain(_world.Runtime.Runs, r => r.Env.ContainsKey("MSSQL_SA_PASSWORD"));
    }
}

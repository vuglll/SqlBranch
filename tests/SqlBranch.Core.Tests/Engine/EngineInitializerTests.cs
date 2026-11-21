using System.Text.Json;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Engine;

public sealed class EngineInitializerTests
{
    private const string DefaultPool = "/home/u/.sqlbranch/pool";
    private const string GivenPassword = "Given#Passw0rd";

    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeEngineConnector _connector = new();
    private readonly SecretRedactor _redactor = new();
    private readonly FakeInitHost _host;

    public EngineInitializerTests()
    {
        _host = new FakeInitHost(_runtime, _connector);
    }

    private static readonly Settings PlainPool = new() { Pool = FakeInitHost.PoolDir, PoolBackend = PoolBackend.Plain };

    private EngineInitializer Initializer(EngineTimings? timings = null) =>
        new(_runtime, _connector, _redactor, ManagedPaths.Default,
            timings ?? new EngineTimings { ReadyTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(10) },
            () => new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    private Task<InitOutcome> InitAsync(Settings settings, string? password = null, EngineTimings? timings = null) =>
        Initializer(timings).InitializeAsync(settings, password, DefaultPool, CancellationToken.None);

    private ContainerSpec EngineRun => _runtime.Runs.Single(r => r.Name == ContainerLabels.EngineName);

    // First init

    [Fact]
    public async Task A_first_init_starts_one_engine_on_loopback_with_the_pool_and_the_documented_labels()
    {
        var outcome = await InitAsync(PlainPool);

        var spec = EngineRun;
        Assert.Equal(ContainerLabels.EngineName, spec.Name);
        Assert.True(spec.Detach);
        Assert.Equal(ImagePlatform.Amd64, spec.Platform);
        Assert.Equal([new PortSpec("127.0.0.1", 14330, 1433)], spec.Ports);
        Assert.Equal("plain", spec.Labels[ContainerLabels.Backend]);
        Assert.Equal(FakeInitHost.PoolDir, spec.Labels[ContainerLabels.Pool]);
        Assert.Equal("developer", spec.Labels[ContainerLabels.Edition]);
        Assert.Equal("14330", spec.Labels[ContainerLabels.Port]);
        Assert.Equal("Y", spec.Env["ACCEPT_EULA"]);
        Assert.Equal("Developer", spec.Env["MSSQL_PID"]);
        Assert.Contains(new MountSpec(MountKind.Bind, FakeInitHost.PoolDir, "/pool"), spec.Mounts);
        Assert.Contains(new MountSpec(MountKind.Bind, FakeInitHost.PoolDir, "/var/opt/mssql", SubPath: "engine"), spec.Mounts);
        Assert.Equal("16.0.4325.4", outcome.Version);
        Assert.Equal("Developer Edition (64-bit)", outcome.Edition);
        Assert.False(outcome.Repeated);
        Assert.Contains("Engine container: sqlbranch-engine", outcome.Created);
    }

    [Fact]
    public async Task The_engine_starts_with_a_bootstrap_password_that_init_then_replaces()
    {
        await InitAsync(PlainPool);

        var bootstrap = EngineRun.Env["MSSQL_SA_PASSWORD"];
        var stored = JsonDocument.Parse(_host.CredentialsJson!).RootElement.GetProperty("password").GetString()!;
        Assert.NotEqual(bootstrap, stored);
        Assert.Equal(stored, _connector.SaPassword);
        Assert.True(PasswordPolicy.IsAcceptable(bootstrap));
        Assert.True(PasswordPolicy.IsAcceptable(stored));
        Assert.Equal(PasswordGenerator.Length, stored.Length);
        Assert.Contains(_connector.Statements, s => s.StartsWith("ALTER LOGIN sa WITH PASSWORD", StringComparison.Ordinal));
        Assert.Equal(bootstrap, _connector.Attempts.First());
        Assert.Equal(stored, _connector.Attempts.Last());
    }

    [Fact]
    public async Task A_given_password_becomes_the_real_one()
    {
        await InitAsync(PlainPool, GivenPassword);

        Assert.Equal(GivenPassword, _connector.SaPassword);
        Assert.NotEqual(GivenPassword, EngineRun.Env["MSSQL_SA_PASSWORD"]);
        Assert.Contains(GivenPassword, _host.CredentialsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_with_a_quote_is_escaped_in_alter_login()
    {
        await InitAsync(PlainPool, "It's#Passw0rd");

        Assert.Equal("It's#Passw0rd", _connector.SaPassword);
    }

    [Fact]
    public async Task The_real_password_is_never_on_a_command_line_or_in_a_script_or_a_label()
    {
        await InitAsync(PlainPool, GivenPassword);

        foreach (var run in _runtime.Runs)
        {
            var visible = string.Join(' ', DockerRuntime.BuildRunArguments(run));
            Assert.DoesNotContain(GivenPassword, visible, StringComparison.Ordinal);
            Assert.DoesNotContain(GivenPassword, FakeContainerRuntime.ScriptOf(run), StringComparison.Ordinal);
            Assert.DoesNotContain(run.Env.Values, v => v == GivenPassword);
        }

        Assert.DoesNotContain(_runtime.Calls, c => c.Contains(GivenPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_credentials_are_written_with_owner_only_access_by_a_labeled_unprivileged_container()
    {
        await InitAsync(PlainPool);

        var write = _runtime.Runs.Single(r => r.Stdin is not null);
        var script = FakeContainerRuntime.ScriptOf(write);
        Assert.Contains("umask 077", script, StringComparison.Ordinal);
        Assert.Contains("chmod 600 /pool/engine/credentials.json", script, StringComparison.Ordinal);
        Assert.Contains("chown 10001:0 /pool/engine/credentials.json", script, StringComparison.Ordinal);
        Assert.False(write.Privileged);
        Assert.True(write.RemoveWhenDone);
    }

    [Fact]
    public async Task A_weak_given_password_is_refused_before_anything_is_created()
    {
        var ex = await Assert.ThrowsAsync<ConfigException>(() => InitAsync(PlainPool, "abc"));

        Assert.Contains("SQLBRANCH_SA_PASSWORD", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task A_managed_pool_init_lists_the_pool_file_size_mount_point_and_container()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");

        var outcome = await InitAsync(new Settings { PoolBackend = PoolBackend.Auto, PoolSize = new ByteSize(60_000_000_000) });

        Assert.Contains("Pool file: /var/lib/sqlbranch/pool.xfs (60 GB)", outcome.Created);
        Assert.Contains("Mount point: /var/lib/sqlbranch/mnt", outcome.Created);
        Assert.Contains("Engine container: sqlbranch-engine", outcome.Created);
        Assert.Equal("loop-xfs", EngineRun.Labels[ContainerLabels.Backend]);
        Assert.Equal("/var/lib/sqlbranch/mnt", EngineRun.Labels[ContainerLabels.Pool]);
        Assert.Equal(CloneMode.Thin, outcome.Mode);
    }

    [Fact]
    public async Task A_volume_pool_mounts_the_engine_folder_with_a_subpath()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _host.FailMount = true;

        var outcome = await InitAsync(new Settings { PoolBackend = PoolBackend.Auto });

        Assert.Equal(CloneMode.Copy, outcome.Mode);
        Assert.Contains(new MountSpec(MountKind.Volume, "sqlbranch-pool", "/var/opt/mssql", SubPath: "engine"), EngineRun.Mounts);
        Assert.Equal("sqlbranch-pool", EngineRun.Labels[ContainerLabels.Pool]);
        Assert.NotEmpty(outcome.Notes!);
    }

    // The wait

    [Fact]
    public async Task An_engine_that_never_answers_exits_3_with_the_log_and_is_removed()
    {
        _connector.NeverReady = true;
        _redactor.Register("Secret#Value1");
        _runtime.Log = "Starting up\nlogin with Secret#Value1 failed\n";

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.Equal(ErrorCodes.EngineNotReady, ex.Code);
        Assert.Equal(ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
        Assert.Contains("Starting up", ex.Message, StringComparison.Ordinal);
        Assert.Contains("login with *** failed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret#Value1", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Containers);
        Assert.Null(_host.CredentialsJson);
    }

    [Fact]
    public void The_default_wait_is_120_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), EngineTimings.Default.ReadyTimeout);
    }

    [Fact]
    public async Task An_engine_container_that_has_exited_is_reported_without_waiting_the_full_time()
    {
        _connector.NeverReady = true;
        // The container dies after the first login attempt.
        _connector.OnConnect = () =>
            _runtime.Containers[ContainerLabels.EngineName] = _runtime.Containers[ContainerLabels.EngineName] with { Running = false };
        var longWait = new EngineTimings { ReadyTimeout = TimeSpan.FromMinutes(5), PollInterval = TimeSpan.FromMilliseconds(10) };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool, timings: longWait));

        Assert.Equal(ErrorCodes.EngineNotReady, ex.Code);
        Assert.Contains("stopped", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    // Edition (4.14)

    private static Settings WithEdition(EngineEdition edition, string image) =>
        new() { Pool = FakeInitHost.PoolDir, PoolBackend = PoolBackend.Plain, EngineEdition = edition, EngineImage = image };

    [Theory]
    [InlineData(EngineEdition.Developer, "Developer")]
    [InlineData(EngineEdition.StandardDeveloper, "StandardDeveloper")]
    [InlineData(EngineEdition.EnterpriseDeveloper, "EnterpriseDeveloper")]
    public async Task The_edition_reaches_the_engine_as_its_product_id_at_first_start(EngineEdition edition, string pid)
    {
        await InitAsync(WithEdition(edition, "mcr.microsoft.com/mssql/server:2025-latest"));

        Assert.Equal(pid, EngineRun.Env["MSSQL_PID"]);
        Assert.Equal(SettingNames.Of(edition), EngineRun.Labels[ContainerLabels.Edition]);
    }

    [Fact]
    public async Task A_tag_without_a_year_on_a_version_below_2025_removes_what_the_run_created_and_exits_2()
    {
        _connector.Version = "16.0.4325.4|Developer Edition (64-bit)";

        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            InitAsync(WithEdition(EngineEdition.StandardDeveloper, "registry.local/mssql:team")));

        Assert.Contains("2025", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Everything this run created was removed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Containers);
        Assert.Null(_host.CredentialsJson);
        Assert.False(_host.EngineDirExists);
        Assert.False(_host.MarkerExists);
    }

    [Fact]
    public async Task An_old_engine_that_refuses_the_product_id_at_start_exits_2_and_everything_is_removed()
    {
        _connector.NeverReady = true;
        _runtime.Log = "ERROR: The provided PID [StandardDeveloper] is invalid.";
        _connector.OnConnect = () =>
            _runtime.Containers[ContainerLabels.EngineName] = _runtime.Containers[ContainerLabels.EngineName] with { Running = false };

        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            InitAsync(WithEdition(EngineEdition.StandardDeveloper, "registry.local/mssql:team")));

        Assert.Contains("2025", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Containers);
        Assert.False(_host.MarkerExists);
    }

    [Fact]
    public async Task A_tag_without_a_year_on_a_managed_pool_undoes_the_pool_too()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _connector.Version = "16.0.4325.4|Developer Edition (64-bit)";

        await Assert.ThrowsAsync<ConfigException>(() =>
            InitAsync(new Settings { PoolBackend = PoolBackend.Auto, PoolSize = new ByteSize(60_000_000_000), EngineEdition = EngineEdition.EnterpriseDeveloper, EngineImage = "registry.local/mssql:team" }));

        Assert.Empty(_runtime.Containers);
        Assert.False(_host.PoolFileExists);
        Assert.False(_host.Mounted);
    }

    [Fact]
    public async Task A_tag_without_a_year_on_version_2025_is_accepted()
    {
        _connector.Version = "17.0.1000.7|Standard Developer Edition (64-bit)";

        var outcome = await InitAsync(WithEdition(EngineEdition.StandardDeveloper, "registry.local/mssql:team"));

        Assert.Equal("Standard Developer Edition (64-bit)", outcome.Edition);
        Assert.Single(_runtime.Containers);
    }

    [Fact]
    public async Task The_developer_edition_needs_no_version_check()
    {
        _connector.Version = "15.0.4000.1|Developer Edition (64-bit)";

        var outcome = await InitAsync(WithEdition(EngineEdition.Developer, "registry.local/mssql:team"));

        Assert.Equal("15.0.4000.1", outcome.Version);
    }

    // Copy-on-write hint (4.16)

    [Fact]
    public async Task A_thin_pool_gets_the_8_KiB_hint_on_the_branches_folder_from_an_unprivileged_container_that_mounts_only_the_pool()
    {
        _host.DirectoryReflink = true;

        var outcome = await InitAsync(PlainPool);

        var spec = _runtime.Runs.Single(r => FakeContainerRuntime.ScriptOf(r).Contains("cowextsize 8k", StringComparison.Ordinal));
        Assert.False(spec.Privileged);
        Assert.False(spec.HostPid);
        Assert.True(spec.RemoveWhenDone);
        Assert.Equal([new MountSpec(MountKind.Bind, FakeInitHost.PoolDir, "/pool", false)], spec.Mounts);
        Assert.Contains("/pool/branches", FakeContainerRuntime.ScriptOf(spec), StringComparison.Ordinal);
        Assert.True(_host.BranchesDirExists);
        Assert.True(_host.CowHintSet);
        Assert.True(outcome.Notes is null or { Count: 0 });
    }

    [Fact]
    public async Task A_copy_pool_gets_no_hint()
    {
        _host.DirectoryReflink = false;

        await InitAsync(PlainPool);

        Assert.DoesNotContain(_host.Scripts, s => s.Contains("cowextsize", StringComparison.Ordinal));
        Assert.False(_host.CowHintSet);
    }

    [Fact]
    public async Task A_pool_that_cannot_take_the_hint_still_initializes_and_says_so()
    {
        _host.DirectoryReflink = true;
        _host.CowHintSupported = false;

        var outcome = await InitAsync(PlainPool);

        Assert.Contains(outcome.Notes!, n => n.Contains("copy-on-write hint", StringComparison.Ordinal));
        Assert.Single(_runtime.Containers);
    }

    [Fact]
    public async Task A_failed_first_init_on_a_thin_plain_pool_removes_the_branches_folder_it_created()
    {
        _host.DirectoryReflink = true;
        _connector.FailAlterLogin = true;

        await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.False(_host.BranchesDirExists);
    }

    [Fact]
    public async Task A_repeated_init_does_not_touch_the_hint()
    {
        _host.DirectoryReflink = true;
        await InitAsync(PlainPool);
        var before = _host.Scripts.Count(s => s.Contains("cowextsize", StringComparison.Ordinal));

        await InitAsync(PlainPool);

        Assert.Equal(before, _host.Scripts.Count(s => s.Contains("cowextsize", StringComparison.Ordinal)));
    }

    // Undo

    public static TheoryData<string> FailingSteps => new() { "enginedir", "engine", "credentials", "alter", "version" };

    private void Break(string step)
    {
        switch (step)
        {
            case "alter":
                _connector.FailAlterLogin = true;
                break;
            case "version":
                _connector.FailVersion = true;
                break;
            default:
                _host.FailStep = step;
                break;
        }
    }

    [Theory]
    [MemberData(nameof(FailingSteps))]
    public async Task A_failure_after_each_step_on_a_managed_pool_undoes_everything_and_exits_1(string step)
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        Break(step);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            InitAsync(new Settings { PoolBackend = PoolBackend.Auto, PoolSize = new ByteSize(60_000_000_000) }));

        Assert.Equal(ErrorCodes.InitFailed, ex.Code);
        Assert.Equal(ExitCode.OperationFailed, ex.Code.ExitCode);
        Assert.Contains("Everything this run created was removed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Containers);
        Assert.False(_host.PoolFileExists);
        Assert.False(_host.Mounted);
        Assert.Empty(_runtime.Volumes);
    }

    [Theory]
    [MemberData(nameof(FailingSteps))]
    public async Task A_failure_after_each_step_on_a_plain_pool_removes_what_the_run_put_into_it(string step)
    {
        Break(step);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.Equal(ErrorCodes.InitFailed, ex.Code);
        Assert.Empty(_runtime.Containers);
        Assert.Null(_host.CredentialsJson);
        Assert.False(_host.EngineDirExists);
        Assert.False(_host.MarkerExists);
    }

    [Fact]
    public async Task Undo_leaves_a_marker_and_engine_folder_that_existed_before()
    {
        _host.MarkerExists = true;
        _host.EngineDirExists = true;
        _connector.FailAlterLogin = true;

        await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.True(_host.MarkerExists);
        Assert.True(_host.EngineDirExists);
        Assert.Null(_host.CredentialsJson);
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task A_failed_volume_fallback_init_removes_the_volume_it_created()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _host.FailMount = true;
        _connector.FailVersion = true;

        await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(new Settings { PoolBackend = PoolBackend.Auto }));

        Assert.Empty(_runtime.Volumes);
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task A_failed_undo_step_is_named_and_does_not_stop_the_rest()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _connector.FailVersion = true;
        var inner = _runtime.RunHandler;
        _runtime.RunHandler = spec =>
            FakeContainerRuntime.ScriptOf(spec).Contains("umount", StringComparison.Ordinal)
                ? FakeContainerRuntime.Fail(32, "umount: target is busy")
                : inner(spec);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            InitAsync(new Settings { PoolBackend = PoolBackend.Auto, PoolSize = new ByteSize(60_000_000_000) }));

        Assert.Equal(ErrorCodes.InitFailed, ex.Code);
        Assert.Empty(_runtime.Containers);
        Assert.Contains("/var/lib/sqlbranch/mnt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("teardown --force", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configuration_cause_keeps_exit_2_but_still_undoes()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _host.HostFree = 1_000_000_000;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            InitAsync(new Settings { PoolBackend = PoolBackend.LoopXfs, PoolSize = new ByteSize(60_000_000_000) }));

        Assert.Equal(ErrorCodes.HostDiskFull, ex.Code);
        Assert.Empty(_runtime.Containers);
        Assert.False(_host.PoolFileExists);
    }

    // Repeat and refusals

    [Fact]
    public async Task A_repeated_init_keeps_everything_and_starts_a_stopped_engine()
    {
        _host.AddExistingEngine("mcr.microsoft.com/mssql/server:2022-latest", "plain", FakeInitHost.PoolDir, running: false);
        _runtime.RunHandler = RepeatRun;

        var outcome = await InitAsync(PlainPool);

        Assert.True(outcome.Repeated);
        Assert.Contains("start sqlbranch-engine", _runtime.Calls);
        Assert.Empty(outcome.Created);
        Assert.DoesNotContain(_runtime.Runs, r => r.Detach);
        Assert.DoesNotContain(_host.Scripts, s => s.Contains("chown -R", StringComparison.Ordinal));
        Assert.DoesNotContain(_connector.Statements, s => s.StartsWith("ALTER LOGIN", StringComparison.Ordinal));
        Assert.Equal("Stored#Pass1", _connector.Attempts.First());
    }

    private ContainerResult RepeatRun(ContainerSpec spec)
    {
        var script = FakeContainerRuntime.ScriptOf(spec);
        if (script.Contains("SQLBRANCH_CREDENTIALS=", StringComparison.Ordinal))
        {
            return _host.CredentialsReply();
        }

        return FakeContainerRuntime.Ok();
    }

    [Fact]
    public async Task A_repeated_init_on_a_running_engine_changes_nothing()
    {
        _host.AddExistingEngine("mcr.microsoft.com/mssql/server:2022-latest", "plain", FakeInitHost.PoolDir);
        _runtime.RunHandler = RepeatRun;

        var outcome = await InitAsync(PlainPool);

        Assert.True(outcome.Repeated);
        Assert.DoesNotContain("start sqlbranch-engine", _runtime.Calls);
        Assert.DoesNotContain("rm sqlbranch-engine", _runtime.Calls);
    }

    [Fact]
    public async Task A_foreign_container_with_the_engines_name_exits_3_and_is_left_alone()
    {
        var foreign = new ContainerInfo(ContainerLabels.EngineName, "postgres:16", true, new Dictionary<string, string>(), ["0.0.0.0:5432"]);
        _runtime.Containers[ContainerLabels.EngineName] = foreign;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
        Assert.Equal(ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
        Assert.Contains("sqlbranch-engine", ex.Message, StringComparison.Ordinal);
        Assert.Equal(foreign, _runtime.Containers[ContainerLabels.EngineName]);
        Assert.Empty(_runtime.Runs);
        Assert.DoesNotContain(_runtime.Calls, c => c.StartsWith("rm ", StringComparison.Ordinal) || c.StartsWith("stop ", StringComparison.Ordinal) || c.StartsWith("start ", StringComparison.Ordinal));
    }

    public static TheoryData<string, string> Changes => new()
    {
        { SettingKeys.EngineImage, "mcr.microsoft.com/mssql/server:2025-latest" },
        { SettingKeys.EngineEdition, "enterprise-developer" },
        { SettingKeys.Port, "14331" },
        { SettingKeys.Pool, "/data/other" },
        { SettingKeys.SaPasswordVariable, "Other#Passw0rd" },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_changed_fixed_setting_exits_2_and_starts_nothing(string key, string value)
    {
        _host.AddExistingEngine("mcr.microsoft.com/mssql/server:2022-latest", "plain", FakeInitHost.PoolDir, running: false);
        _runtime.RunHandler = RepeatRun;
        var settings = key switch
        {
            SettingKeys.EngineImage => PlainPool with { EngineImage = value },
            SettingKeys.EngineEdition => PlainPool with { EngineEdition = EngineEdition.EnterpriseDeveloper },
            SettingKeys.Port => PlainPool with { Port = 14331 },
            SettingKeys.Pool => PlainPool with { Pool = value },
            _ => PlainPool,
        };

        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            InitAsync(settings, key == SettingKeys.SaPasswordVariable ? value : null));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Other#Passw0rd", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("start sqlbranch-engine", _runtime.Calls);
        Assert.Empty(_connector.Attempts);
    }

    [Fact]
    public async Task A_repeated_init_remounts_a_managed_pool_before_starting_the_engine()
    {
        _host.AddExistingEngine("mcr.microsoft.com/mssql/server:2022-latest", "loop-xfs", "/var/lib/sqlbranch/mnt", running: false);
        _runtime.RunHandler = spec =>
        {
            if (spec.Image == ManagedPool.HelperImage)
            {
                var script = FakeContainerRuntime.ScriptOf(spec);
                return script.Contains("SQLBRANCH_MOUNT", StringComparison.Ordinal)
                    ? FakeContainerRuntime.Ok("SQLBRANCH_MOUNT=no")
                    : FakeContainerRuntime.Ok();
            }

            return RepeatRun(spec);
        };

        await InitAsync(PlainPool);

        var mount = _runtime.Calls.FindIndex(c => c.StartsWith("run sqlbranch-helper-mount", StringComparison.Ordinal));
        var start = _runtime.Calls.IndexOf("start sqlbranch-engine");
        Assert.True(mount >= 0 && mount < start);
    }

    [Fact]
    public async Task A_failed_remount_exits_3_pool_not_mounted_and_does_not_start_the_engine()
    {
        _host.AddExistingEngine("mcr.microsoft.com/mssql/server:2022-latest", "loop-xfs", "/var/lib/sqlbranch/mnt", running: false);
        _runtime.RunHandler = spec =>
        {
            var script = FakeContainerRuntime.ScriptOf(spec);
            if (script.Contains("SQLBRANCH_MOUNT", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_MOUNT=no");
            }

            return script.Contains("mount -o loop", StringComparison.Ordinal) ? FakeContainerRuntime.Fail(32, "mount failed") : RepeatRun(spec);
        };

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.Equal(ErrorCodes.PoolNotMounted, ex.Code);
        Assert.DoesNotContain("start sqlbranch-engine", _runtime.Calls);
    }

    [Fact]
    public async Task An_engine_recreated_over_an_existing_pool_logs_in_with_the_stored_password_and_never_puts_it_in_the_container_environment()
    {
        _host.CredentialsJson = "{\"user\":\"sa\",\"password\":\"Stored#Pass1\"}";
        _host.EngineDirExists = true;
        _host.MarkerExists = true;
        _connector.SaPassword = "Stored#Pass1";

        var outcome = await InitAsync(PlainPool);

        Assert.False(outcome.Repeated);
        Assert.NotEqual("Stored#Pass1", EngineRun.Env["MSSQL_SA_PASSWORD"]);
        Assert.True(PasswordPolicy.IsAcceptable(EngineRun.Env["MSSQL_SA_PASSWORD"]));
        Assert.DoesNotContain(_runtime.Runs.SelectMany(r => r.Env.Values), v => v == "Stored#Pass1");
        Assert.Contains("Stored#Pass1", _connector.Attempts);
        Assert.DoesNotContain(_connector.Statements, s => s.StartsWith("ALTER LOGIN", StringComparison.Ordinal));
        Assert.Equal("Stored#Pass1", _connector.SaPassword);
    }

    [Fact]
    public async Task When_the_stored_login_fails_but_the_bootstrap_login_works_the_stored_password_is_set_with_alter_login()
    {
        _host.CredentialsJson = "{\"user\":\"sa\",\"password\":\"Stored#Pass1\"}";
        _host.EngineDirExists = true;
        _host.MarkerExists = true;
        _connector.SaPassword = null;
        var runHandler = _runtime.RunHandler;
        _runtime.RunHandler = spec =>
        {
            var result = runHandler(spec);
            if (spec.Detach)
            {
                // The system databases were lost, so the engine starts with the bootstrap password after all.
                _connector.SaPassword = spec.Env["MSSQL_SA_PASSWORD"];
            }

            return result;
        };

        await InitAsync(PlainPool);

        var bootstrap = EngineRun.Env["MSSQL_SA_PASSWORD"];
        Assert.NotEqual("Stored#Pass1", bootstrap);
        Assert.Equal("Stored#Pass1", _connector.SaPassword);
        Assert.Contains(_connector.Statements, s => s.StartsWith("ALTER LOGIN sa WITH PASSWORD = N'Stored#Pass1'", StringComparison.Ordinal));
        Assert.DoesNotContain(_runtime.Runs.SelectMany(r => r.Env.Values), v => v == "Stored#Pass1");
        // The credentials file is not rewritten: it already held that password.
        Assert.DoesNotContain(_runtime.Runs, r => r.Stdin is not null);
    }

    [Fact]
    public async Task No_container_environment_holds_the_real_password_on_a_first_init()
    {
        await InitAsync(PlainPool, GivenPassword);

        Assert.DoesNotContain(_runtime.Runs.SelectMany(r => r.Env.Values), v => v == GivenPassword);
        var stored = JsonDocument.Parse(_host.CredentialsJson!).RootElement.GetProperty("password").GetString()!;
        Assert.DoesNotContain(_runtime.Runs.SelectMany(r => r.Env.Values), v => v == stored || v.Contains(stored, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_recreate_leaves_the_existing_credentials_and_engine_folder()
    {
        _host.CredentialsJson = "{\"user\":\"sa\",\"password\":\"Stored#Pass1\"}";
        _host.EngineDirExists = true;
        _host.MarkerExists = true;
        _connector.SaPassword = "Stored#Pass1";
        _connector.FailVersion = true;

        await Assert.ThrowsAsync<SqlBranchException>(() => InitAsync(PlainPool));

        Assert.NotNull(_host.CredentialsJson);
        Assert.True(_host.EngineDirExists);
        Assert.Empty(_runtime.Containers);
    }
}

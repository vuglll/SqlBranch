using System.ComponentModel;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Containers;

public sealed class DockerRuntimeTests
{
    private readonly FakeProcessRunner _runner = new();
    private readonly SecretRedactor _redactor = new();

    private DockerRuntime Runtime() => new(_runner, _redactor);

    [Fact]
    public void Run_arguments_carry_the_managed_label_and_every_option()
    {
        var spec = new ContainerSpec
        {
            Name = "sqlbranch-engine",
            Image = "mcr.microsoft.com/mssql/server:2022-latest",
            Detach = true,
            Labels = new Dictionary<string, string> { ["io.sqlbranch.backend"] = "plain" },
            Env = new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = "Bootstrap#1" },
            Ports = [new PortSpec("127.0.0.1", 14330, 1433)],
            Mounts = [new MountSpec(MountKind.Bind, "/data/sb", "/pool")],
        };

        var args = DockerRuntime.BuildRunArguments(spec);

        Assert.Equal(
            ["run", "-d", "--name", "sqlbranch-engine",
             "--label", "io.sqlbranch.backend=plain", "--label", "io.sqlbranch.managed=true",
             "-e", "MSSQL_SA_PASSWORD", "-p", "127.0.0.1:14330:1433", "-v", "/data/sb:/pool",
             "mcr.microsoft.com/mssql/server:2022-latest"],
            args);
    }

    [Fact]
    public void Platform_entrypoint_user_and_subpath_mounts_reach_the_command_line()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            Image = "img",
            Platform = "linux/amd64",
            Entrypoint = string.Empty,
            User = "0",
            Mounts =
            [
                new MountSpec(MountKind.Bind, "/var/lib/sqlbranch/mnt", "/var/opt/mssql", SubPath: "engine"),
                new MountSpec(MountKind.Volume, "sqlbranch-pool", "/var/opt/mssql", SubPath: "engine"),
            ],
        };

        var args = DockerRuntime.BuildRunArguments(spec);

        Assert.Equal(["--platform", "linux/amd64"], args.Skip(args.ToList().IndexOf("--platform")).Take(2));
        Assert.Equal(["--entrypoint", string.Empty], args.Skip(args.ToList().IndexOf("--entrypoint")).Take(2));
        Assert.Contains("/var/lib/sqlbranch/mnt/engine:/var/opt/mssql", args);
        Assert.Contains("type=volume,src=sqlbranch-pool,dst=/var/opt/mssql,volume-subpath=engine", args);
    }

    [Fact]
    public void Only_the_sql_server_image_gets_a_platform()
    {
        Assert.Equal("linux/amd64", ImagePlatform.For("mcr.microsoft.com/mssql/server:2022-latest"));
        Assert.Null(ImagePlatform.For("ubuntu:24.04"));
    }

    [Fact]
    public void The_managed_label_cannot_be_overridden_or_left_out()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            Image = "img",
            Labels = new Dictionary<string, string> { ["io.sqlbranch.managed"] = "false" },
        };

        var args = DockerRuntime.BuildRunArguments(spec);

        Assert.Contains("io.sqlbranch.managed=true", args);
        Assert.DoesNotContain("io.sqlbranch.managed=false", args);
    }

    [Fact]
    public void Privileged_helper_arguments()
    {
        var spec = new ContainerSpec
        {
            Name = "h",
            Image = "ubuntu:24.04",
            Privileged = true,
            HostPid = true,
            RemoveWhenDone = true,
            Mounts = [new MountSpec(MountKind.Bind, "/var/lib/sqlbranch", "/var/lib/sqlbranch")],
            Command = ["sh", "-c", "true"],
        };

        var args = string.Join(' ', DockerRuntime.BuildRunArguments(spec));

        Assert.Equal(
            "run --rm --name h --label io.sqlbranch.managed=true --privileged --pid host -v /var/lib/sqlbranch:/var/lib/sqlbranch ubuntu:24.04 sh -c true",
            args);
    }

    [Fact]
    public void Read_only_mounts_end_with_ro()
    {
        var spec = new ContainerSpec { Name = "n", Image = "i", Mounts = [new MountSpec(MountKind.Volume, "sqlbranch-pool", "/pool", ReadOnly: true)] };

        Assert.Contains("sqlbranch-pool:/pool:ro", DockerRuntime.BuildRunArguments(spec));
    }

    [Fact]
    public async Task Stdin_adds_the_interactive_flag_and_goes_to_the_process_not_to_the_command_line()
    {
        var spec = new ContainerSpec { Name = "w", Image = "i", RemoveWhenDone = true, Stdin = "{\"password\":\"Secret#1\"}", Command = ["sh", "-c", "cat > /f"] };

        await Runtime().RunAsync(spec, CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.Contains("-i", call.Args);
        Assert.DoesNotContain("Secret#1", string.Join(' ', call.Args), StringComparison.Ordinal);
        Assert.Equal("{\"password\":\"Secret#1\"}", Assert.Single(_runner.Inputs));
        Assert.DoesNotContain("-i", DockerRuntime.BuildRunArguments(new ContainerSpec { Name = "n", Image = "i" }));
    }

    [Fact]
    public async Task Secret_values_go_in_the_process_environment_not_on_the_command_line()
    {
        var spec = new ContainerSpec
        {
            Name = "e",
            Image = "i",
            Detach = true,
            Env = new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = "Bootstrap#1" },
        };

        await Runtime().RunAsync(spec, CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.DoesNotContain("Bootstrap#1", string.Join(' ', call.Args), StringComparison.Ordinal);
        Assert.Equal("Bootstrap#1", call.Env!["MSSQL_SA_PASSWORD"]);
    }

    [Fact]
    public async Task Exec_into_an_unlabeled_container_is_refused_before_any_exec()
    {
        _runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: false), string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            Runtime().ExecAsync("sqlbranch-engine", ["ls"], CancellationToken.None));

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
        Assert.DoesNotContain(_runner.Lines, l => l.StartsWith("exec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_stop_remove_and_logs_refuse_an_unlabeled_container()
    {
        _runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("other", managed: false), string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);
        var runtime = Runtime();

        await Assert.ThrowsAsync<SqlBranchException>(() => runtime.StartAsync("other", CancellationToken.None));
        await Assert.ThrowsAsync<SqlBranchException>(() => runtime.StopAsync("other", CancellationToken.None));
        await Assert.ThrowsAsync<SqlBranchException>(() => runtime.RemoveAsync("other", CancellationToken.None));
        await Assert.ThrowsAsync<SqlBranchException>(() => runtime.LogsAsync("other", 20, CancellationToken.None));

        Assert.All(_runner.Lines, l => Assert.StartsWith("inspect", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_labeled_container_is_acted_on()
    {
        _runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: true), string.Empty)
            : new ProcessOutput(0, "ok", string.Empty);

        var result = await Runtime().ExecAsync("sqlbranch-engine", ["ls", "/pool"], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("exec sqlbranch-engine ls /pool", _runner.Lines);
    }

    [Fact]
    public async Task Copying_into_a_labeled_container_runs_docker_cp_then_fixes_the_owner_as_root()
    {
        _runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: true), string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);

        await Runtime().CopyToAsync("sqlbranch-engine", "/home/me/shop.bak", "/pool/build/x/backup.bak", "10001:0", CancellationToken.None);

        Assert.Contains("cp -L /home/me/shop.bak sqlbranch-engine:/pool/build/x/backup.bak", _runner.Lines);
        var fix = _runner.Calls.Single(c => c.Args[0] == "exec").Args;
        Assert.Equal(["exec", "--user", "0", "sqlbranch-engine", "sh", "-c", "[ -f \"$2\" ] && [ ! -L \"$2\" ] && chown \"$1\" \"$2\" && chmod 640 \"$2\"", "sh", "10001:0", "/pool/build/x/backup.bak"], fix);
    }

    [Fact]
    public async Task Copying_into_an_unlabeled_container_is_refused_before_any_copy()
    {
        _runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("other", managed: false), string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            Runtime().CopyToAsync("other", "/home/me/shop.bak", "/pool/x", "10001:0", CancellationToken.None));

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
        Assert.DoesNotContain(_runner.Lines, l => l.StartsWith("cp", StringComparison.Ordinal) || l.StartsWith("exec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_copy_throws_with_the_dockers_message_and_does_not_chown()
    {
        _runner.Handler = a => a[0] switch
        {
            "inspect" => new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: true), string.Empty),
            "cp" => new ProcessOutput(1, string.Empty, "Error: no space left on device"),
            _ => new ProcessOutput(0, string.Empty, string.Empty),
        };

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            Runtime().CopyToAsync("sqlbranch-engine", "/home/me/shop.bak", "/pool/x", "10001:0", CancellationToken.None));

        Assert.Contains("no space left", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Lines, l => l.StartsWith("exec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_missing_container_returns_false()
    {
        _runner.Handler = _ => new ProcessOutput(1, string.Empty, "Error: No such container: gone");

        Assert.False(await Runtime().RemoveAsync("gone", CancellationToken.None));
    }

    [Fact]
    public async Task Inspect_reads_labels_state_and_published_ports()
    {
        _runner.Handler = _ => new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: true), string.Empty);

        var info = await Runtime().InspectAsync("sqlbranch-engine", CancellationToken.None);

        Assert.NotNull(info);
        Assert.True(info.IsManaged);
        Assert.True(info.Running);
        Assert.Equal(["127.0.0.1:14330"], info.PublishedPorts);
    }

    [Fact]
    public async Task Volumes_are_created_with_the_label_and_foreign_volumes_are_not_removed()
    {
        var runtime = Runtime();
        await runtime.CreateVolumeAsync("sqlbranch-pool", CancellationToken.None);
        Assert.Contains("volume create --label io.sqlbranch.managed=true sqlbranch-pool", _runner.Lines);

        _runner.Handler = a => a.Contains("--format")
            ? new ProcessOutput(0, "{}", string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => runtime.RemoveVolumeAsync("mine", CancellationToken.None));
        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
        Assert.DoesNotContain("volume rm mine", _runner.Lines);
    }

    [Theory]
    [InlineData("{\"io.sqlbranch.managed\":\"true\"}", true)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("{\"io.sqlbranch.managed\":\"false\"}", false)]
    public async Task A_volume_is_managed_only_with_the_label_true(string labels, bool managed)
    {
        _runner.Handler = _ => new ProcessOutput(0, labels, string.Empty);

        Assert.Equal(managed, await Runtime().VolumeIsManagedAsync("sqlbranch-pool", CancellationToken.None));
    }

    [Fact]
    public async Task A_volume_that_does_not_exist_is_not_managed()
    {
        _runner.Handler = _ => new ProcessOutput(1, string.Empty, "Error response from daemon: get x: no such volume");

        Assert.False(await Runtime().VolumeIsManagedAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Image_check_inspects_the_image_and_never_pulls()
    {
        var runtime = Runtime();
        _runner.Handler = _ => new ProcessOutput(0, "sha256:abc", string.Empty);
        Assert.True(await runtime.ImageExistsAsync("img:1", CancellationToken.None));

        _runner.Handler = _ => new ProcessOutput(1, string.Empty, "Error: No such image: img:1");
        Assert.False(await runtime.ImageExistsAsync("img:1", CancellationToken.None));

        Assert.All(_runner.Lines, line => Assert.StartsWith("image inspect --format {{.Id}} img:1", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_docker_command_is_docker_unavailable()
    {
        _runner.Throw = new Win32Exception("No such file or directory");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Runtime().GetInfoAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.DockerUnavailable, ex.Code);
        Assert.Contains("docker", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_daemon_is_docker_unavailable()
    {
        _runner.Handler = _ => new ProcessOutput(1, string.Empty, "failed to connect to the docker API at unix:///var/run/docker.sock");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Runtime().GetInfoAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.DockerUnavailable, ex.Code);
    }

    [Fact]
    public async Task Output_and_errors_pass_through_the_redactor()
    {
        _redactor.Register("Top$ecret1");
        _runner.Handler = _ => new ProcessOutput(1, "log Top$ecret1", "boom Top$ecret1");

        var result = await Runtime().RunAsync(new ContainerSpec { Name = "n", Image = "i" }, CancellationToken.None);

        Assert.Equal("log ***", result.StdOut);
        Assert.Equal("boom ***", result.StdErr);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Runtime().GetInfoAsync(CancellationToken.None));
        Assert.DoesNotContain("Top$ecret1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_reports_docker_desktop()
    {
        _runner.Handler = _ => new ProcessOutput(0, "Docker Desktop|aarch64|docker-desktop\n", string.Empty);

        var info = await Runtime().GetInfoAsync(CancellationToken.None);

        Assert.True(info.IsDockerDesktop);
        Assert.Equal("aarch64", info.Architecture);
    }
}

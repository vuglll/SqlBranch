using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Pools;

public sealed class ManagedPoolTests
{
    private readonly FakeContainerRuntime _runtime = new();
    private readonly ManagedPaths _paths = ManagedPaths.Default;
    private static readonly ByteSize Sixty = new(60_000_000_000);

    private ManagedPool Pool() => new(_runtime, _paths);

    /// <summary>Answers each helper by what its script does. <paramref name="fail"/> names a step to fail.</summary>
    private void Host(long freeBytes = 100_000_000_000, bool fileExists = false, string? fail = null, string failWith = "boom")
    {
        _runtime.RunHandler = spec =>
        {
            var script = FakeContainerRuntime.ScriptOf(spec);
            if (fail is not null && script.Contains(fail, StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Fail(125, failWith);
            }

            return script switch
            {
                var s when s.Contains("df -B1", StringComparison.Ordinal) => FakeContainerRuntime.Ok(freeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"),
                var s when s.Contains("SQLBRANCH_FILE", StringComparison.Ordinal) => FakeContainerRuntime.Ok(fileExists ? "SQLBRANCH_FILE=yes" : "SQLBRANCH_FILE=no"),
                _ => FakeContainerRuntime.Ok(),
            };
        };
    }

    private IEnumerable<string> Scripts => _runtime.Runs.Select(FakeContainerRuntime.ScriptOf);

    [Fact]
    public async Task Too_little_host_space_is_exit_3_host_disk_full_naming_both_numbers_and_creates_nothing()
    {
        Host(freeBytes: 20_000_000_000);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Pool().EnsureHostSpaceAsync(Sixty, CancellationToken.None));

        Assert.Equal(ErrorCodes.HostDiskFull, ex.Code);
        Assert.Equal(ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
        Assert.Contains("60 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20 GB", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Scripts, s => s.Contains("mkfs", StringComparison.Ordinal) || s.Contains("truncate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enough_host_space_passes()
    {
        Host(freeBytes: 60_000_000_000);

        await Pool().EnsureHostSpaceAsync(Sixty, CancellationToken.None);
    }

    [Fact]
    public void The_plan_names_the_pool_file_its_size_and_the_mount_point()
    {
        Assert.Equal(
            ["Pool file: /var/lib/sqlbranch/pool.xfs (60 GB)", "Mount point: /var/lib/sqlbranch/mnt"],
            Pool().DescribePlan(Sixty));
    }

    [Fact]
    public async Task Create_reports_the_plan_before_it_runs_any_privileged_step_then_formats_and_mounts()
    {
        Host();
        var order = new List<string>();
        var runCountAtReport = -1;

        await Pool().CreateAsync(Sixty, line =>
        {
            order.Add(line);
            if (runCountAtReport < 0)
            {
                runCountAtReport = _runtime.Runs.Count(r => r.Privileged);
            }
        }, CancellationToken.None);

        Assert.Equal(0, runCountAtReport);
        Assert.Equal(2, order.Count);
        var privileged = _runtime.Runs.Where(r => r.Privileged).Select(FakeContainerRuntime.ScriptOf).ToList();
        Assert.Contains("mkfs.xfs -q -m reflink=1 '/var/lib/sqlbranch/pool.xfs'", privileged[0], StringComparison.Ordinal);
        Assert.Contains("truncate -s 60000000000 '/var/lib/sqlbranch/pool.xfs'", privileged[0], StringComparison.Ordinal);
        Assert.Contains("nsenter -t 1 -m -- mount -o loop '/var/lib/sqlbranch/pool.xfs' '/var/lib/sqlbranch/mnt'", privileged[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_helper_is_labeled_by_the_runtime_mounts_only_the_managed_folder_and_is_removed()
    {
        Host();

        await Pool().CreateAsync(Sixty, null, CancellationToken.None);
        await Pool().IsMountedAsync(CancellationToken.None);
        await Pool().UnmountAsync(CancellationToken.None);
        await Pool().DeleteFileAsync(CancellationToken.None);

        Assert.NotEmpty(_runtime.Runs);
        Assert.All(_runtime.Runs, run =>
        {
            Assert.True(run.RemoveWhenDone);
            Assert.StartsWith("sqlbranch-helper-", run.Name, StringComparison.Ordinal);
            Assert.Equal(new MountSpec(MountKind.Bind, "/var/lib/sqlbranch", "/var/lib/sqlbranch"), Assert.Single(run.Mounts));
            Assert.Contains("io.sqlbranch.managed=true", DockerRuntime.BuildRunArguments(run));
        });
        Assert.Empty(_runtime.Containers);
        Assert.Equal(_runtime.Runs.Count, _runtime.Calls.Count(c => c.StartsWith("rm sqlbranch-helper-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_failed_mount_removes_the_file_and_every_helper_and_names_the_step()
    {
        Host(fail: "mount -o loop", failWith: "mount: permission denied");

        var ex = await Assert.ThrowsAsync<ManagedPoolException>(() => Pool().CreateAsync(Sixty, null, CancellationToken.None));

        Assert.Equal(ManagedPoolStep.Mount, ex.Step);
        Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Scripts, s => s.Contains("umount", StringComparison.Ordinal));
        Assert.Contains(Scripts, s => s.Contains("rm -f '/var/lib/sqlbranch/pool.xfs'", StringComparison.Ordinal));
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task A_refused_privileged_container_fails_the_format_step_and_leaves_nothing()
    {
        _runtime.RunHandler = spec => spec.Privileged
            ? FakeContainerRuntime.Fail(125, "docker: privileged containers are not allowed")
            : FakeContainerRuntime.Ok(FakeContainerRuntime.ScriptOf(spec).Contains("SQLBRANCH_FILE", StringComparison.Ordinal) ? "SQLBRANCH_FILE=no" : string.Empty);

        var ex = await Assert.ThrowsAsync<ManagedPoolException>(() => Pool().CreateAsync(Sixty, null, CancellationToken.None));

        Assert.Equal(ManagedPoolStep.Format, ex.Step);
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task An_existing_pool_file_is_never_formatted_or_deleted_by_create()
    {
        Host(fileExists: true);

        await Assert.ThrowsAsync<SqlBranchException>(() => Pool().CreateAsync(Sixty, null, CancellationToken.None));

        Assert.DoesNotContain(Scripts, s => s.Contains("mkfs", StringComparison.Ordinal) || s.Contains("rm -f", StringComparison.Ordinal));
    }

    private void FormatFails(int exitCode)
    {
        _runtime.RunHandler = spec =>
        {
            var script = FakeContainerRuntime.ScriptOf(spec);
            return script switch
            {
                var s when s.Contains("SQLBRANCH_FILE", StringComparison.Ordinal) => FakeContainerRuntime.Ok("SQLBRANCH_FILE=no"),
                var s when s.Contains("mkfs.xfs", StringComparison.Ordinal) => FakeContainerRuntime.Fail(exitCode, "format failed"),
                _ => FakeContainerRuntime.Ok(),
            };
        };
    }

    [Fact]
    public async Task A_file_that_appears_between_the_check_and_the_format_is_not_ours_and_is_never_deleted()
    {
        FormatFails(ManagedPool.ExitFileExists);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Pool().CreateAsync(Sixty, null, CancellationToken.None));

        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Scripts, s => s.Contains("rm -f", StringComparison.Ordinal) || s.Contains("umount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_format_script_checks_for_the_file_before_it_creates_anything_and_exits_with_a_distinct_code()
    {
        Host();

        await Pool().CreateAsync(Sixty, null, CancellationToken.None);

        var format = Scripts.Single(s => s.Contains("mkfs.xfs", StringComparison.Ordinal));
        Assert.StartsWith("if [ -e '/var/lib/sqlbranch/pool.xfs' ]; then exit 66; fi;", format, StringComparison.Ordinal);
        Assert.True(format.IndexOf("exit 66", StringComparison.Ordinal) < format.IndexOf("truncate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ManagedPool.ExitBeforeCreate)]
    [InlineData(125)]
    public async Task A_format_that_failed_before_the_file_existed_deletes_nothing(int exitCode)
    {
        FormatFails(exitCode);

        await Assert.ThrowsAsync<ManagedPoolException>(() => Pool().CreateAsync(Sixty, null, CancellationToken.None));

        Assert.DoesNotContain(Scripts, s => s.Contains("rm -f", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_format_that_failed_after_the_file_was_created_deletes_it()
    {
        FormatFails(1);

        await Assert.ThrowsAsync<ManagedPoolException>(() => Pool().CreateAsync(Sixty, null, CancellationToken.None));

        Assert.Contains(Scripts, s => s.Contains("rm -f '/var/lib/sqlbranch/pool.xfs'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/var/lib/sqlbranch")]
    [InlineData("/var/lib/sqlbranch/it-1a2b")]
    public void Test_roots_under_the_managed_folder_are_allowed(string root)
    {
        Assert.Equal(root + "/pool.xfs", new ManagedPaths(root).PoolFile);
    }

    [Theory]
    [InlineData("/var/lib")]
    [InlineData("/var/lib/sqlbranch/../docker")]
    [InlineData("/var/lib/sqlbranchx")]
    [InlineData("/var/lib/sqlbranch/a b")]
    [InlineData("/var/lib/sqlbranch/it';rm -rf /;'")]
    [InlineData("/data")]
    public void Roots_outside_the_managed_folder_are_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => new ManagedPaths(root));
    }
}

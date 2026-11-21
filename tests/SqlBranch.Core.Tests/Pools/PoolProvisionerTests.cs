using System.Globalization;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Pools;

public sealed class PoolProvisionerTests
{
    private const string PoolDir = "/data/sb";
    private static readonly ByteSize Sixty = new(60_000_000_000);
    private readonly FakeContainerRuntime _runtime = new();

    // What the fake host looks like. Tests set these before provisioning.
    private bool _directoryReflink;
    private string _directoryState = "empty";
    private bool _refusePrivileged;
    private bool _failMount;
    private long _hostFree = 100_000_000_000;
    private long? _existingFile;
    private bool _mounted;
    private bool _failRemount;
    private bool _failFileCheck;

    private PoolProvisioner Provisioner() => new(_runtime, ManagedPaths.Default, () => new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    private static Settings With(PoolBackend backend) => new() { Pool = PoolDir, PoolBackend = backend, PoolSize = Sixty };

    private PoolProvisioner Host()
    {
        _runtime.RunHandler = spec =>
        {
            var script = FakeContainerRuntime.ScriptOf(spec);
            if (spec.Image == ManagedPool.HelperImage)
            {
                return Helper(spec, script);
            }

            var source = spec.Mounts[0].Source;
            if (script.Contains("SQLBRANCH_POOL=", StringComparison.Ordinal))
            {
                return FakeContainerRuntime.Ok("SQLBRANCH_POOL=" + (source == PoolDir ? _directoryState : "empty"));
            }

            if (script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
            {
                var thin = source == ManagedPaths.Default.MountPoint || (source == PoolDir && _directoryReflink);
                return FakeContainerRuntime.Ok("SQLBRANCH_PROBE=" + (thin ? "thin" : "copy"));
            }

            return FakeContainerRuntime.Ok();
        };
        return Provisioner();
    }

    private ContainerResult Helper(ContainerSpec spec, string script)
    {
        if (script.Contains("df -B1", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(_hostFree.ToString(CultureInfo.InvariantCulture));
        }

        if (script.Contains("SQLBRANCH_FILE", StringComparison.Ordinal))
        {
            if (_failFileCheck)
            {
                return FakeContainerRuntime.Fail(125, "Unable to find image 'ubuntu:24.04' locally: no such host");
            }

            return FakeContainerRuntime.Ok(_existingFile is null ? "SQLBRANCH_FILE=no" : "SQLBRANCH_FILE=yes");
        }

        if (script.Contains("stat -c %s", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(_existingFile!.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (script.Contains("SQLBRANCH_MOUNT", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(_mounted ? "SQLBRANCH_MOUNT=yes" : "SQLBRANCH_MOUNT=no");
        }

        if (spec.Privileged && _refusePrivileged)
        {
            return FakeContainerRuntime.Fail(125, "docker: privileged containers are not allowed by policy");
        }

        if (script.Contains("mkfs.xfs", StringComparison.Ordinal))
        {
            _existingFile = Sixty.Bytes;
        }
        else if (script.Contains("mount -o loop", StringComparison.Ordinal))
        {
            if (_failMount || _failRemount)
            {
                return FakeContainerRuntime.Fail(32, "mount: /var/lib/sqlbranch/mnt: failed to setup loop device");
            }

            _mounted = true;
        }
        else if (script.Contains("umount", StringComparison.Ordinal))
        {
            _mounted = false;
        }
        else if (script.Contains("rm -f", StringComparison.Ordinal))
        {
            _existingFile = null;
        }

        return FakeContainerRuntime.Ok();
    }

    private IEnumerable<string> Scripts => _runtime.Runs.Select(FakeContainerRuntime.ScriptOf);

    private bool RanHelpers => _runtime.Runs.Any(r => r.Privileged);

    private void AssertNothingLeft()
    {
        Assert.Null(_existingFile);
        Assert.False(_mounted);
        Assert.Empty(_runtime.Containers);
    }

    // plain

    [Fact]
    public async Task Plain_uses_the_directory_as_it_is_and_never_runs_a_helper()
    {
        _directoryReflink = true;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Plain), "/home/u/.sqlbranch/pool", CancellationToken.None);

        Assert.Equal("plain", pool.Backend);
        Assert.Equal(CloneMode.Thin, pool.Mode);
        Assert.Equal(new PoolLocation(PoolKind.Directory, PoolDir), pool.Location);
        Assert.False(RanHelpers);
    }

    [Fact]
    public async Task Plain_on_a_directory_without_reflink_is_copy_mode()
    {
        var pool = await Host().ProvisionAsync(With(PoolBackend.Plain), "/home/u/.sqlbranch/pool", CancellationToken.None);

        Assert.Equal(CloneMode.Copy, pool.Mode);
        Assert.Null(pool.FallbackReason);
    }

    [Fact]
    public async Task The_default_directory_is_used_when_pool_is_not_set()
    {
        var pool = await Host().ProvisionAsync(new Settings { PoolBackend = PoolBackend.Plain }, "/home/u/.sqlbranch/pool", CancellationToken.None);

        Assert.Equal("/home/u/.sqlbranch/pool", pool.Location.Source);
    }

    [Fact]
    public async Task A_foreign_directory_is_refused_under_plain_and_under_auto_on_linux()
    {
        _directoryState = "foreign";
        var provisioner = Host();

        await Assert.ThrowsAsync<ConfigException>(() => provisioner.ProvisionAsync(With(PoolBackend.Plain), "x", CancellationToken.None));
        await Assert.ThrowsAsync<ConfigException>(() => provisioner.ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None));

        Assert.False(RanHelpers);
        Assert.DoesNotContain(Scripts, s => s.Contains("chown", StringComparison.Ordinal));
    }

    // auto

    [Fact]
    public async Task Auto_uses_a_reflink_directory_without_a_managed_pool()
    {
        _directoryReflink = true;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal("plain", pool.Backend);
        Assert.Equal(CloneMode.Thin, pool.Mode);
        Assert.False(RanHelpers);
        Assert.False(pool.CreatedManagedPool);
    }

    [Fact]
    public async Task Auto_on_ext4_creates_the_managed_pool_and_reports_thin()
    {
        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal("loop-xfs", pool.Backend);
        Assert.Equal(CloneMode.Thin, pool.Mode);
        Assert.True(pool.CreatedManagedPool);
        Assert.Equal(ManagedPaths.Default.Location, pool.Location);
        Assert.Contains("Pool file: /var/lib/sqlbranch/pool.xfs (60 GB)", pool.Lines);
        Assert.Contains("Mount point: /var/lib/sqlbranch/mnt", pool.Lines);
        Assert.True(_mounted);
        Assert.Empty(_runtime.Containers);
    }

    [Fact]
    public async Task Auto_on_docker_desktop_goes_straight_to_the_managed_pool()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _directoryReflink = true; // must not matter: host folders are not on the VM disk

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal("loop-xfs", pool.Backend);
        Assert.DoesNotContain(_runtime.Runs, r => r.Mounts.Any(m => m.Source == PoolDir));
    }

    [Fact]
    public async Task Auto_falls_back_to_a_copy_directory_when_privileged_containers_are_refused_and_leaves_nothing()
    {
        _refusePrivileged = true;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal("plain", pool.Backend);
        Assert.Equal(CloneMode.Copy, pool.Mode);
        Assert.Equal(new PoolLocation(PoolKind.Directory, PoolDir), pool.Location);
        Assert.Contains("privileged helper", pool.FallbackReason, StringComparison.Ordinal);
        Assert.Contains(pool.Lines, l => l.StartsWith("Falling back to copy mode", StringComparison.Ordinal));
        AssertNothingLeft();
    }

    [Fact]
    public async Task Auto_on_docker_desktop_falls_back_to_the_sqlbranch_pool_volume()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _refusePrivileged = true;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal(new PoolLocation(PoolKind.Volume, "sqlbranch-pool"), pool.Location);
        Assert.True(pool.CreatedVolume);
        Assert.Contains("sqlbranch-pool", _runtime.Volumes);
        Assert.Equal(CloneMode.Copy, pool.Mode);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_failed_mount_after_formatting_removes_the_file_then_falls_back()
    {
        _failMount = true;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal(CloneMode.Copy, pool.Mode);
        Assert.Contains("could not be created or mounted", pool.FallbackReason, StringComparison.Ordinal);
        Assert.Contains(Scripts, s => s.Contains("mkfs.xfs", StringComparison.Ordinal));
        AssertNothingLeft();
    }

    [Theory]
    [InlineData(PoolBackend.Auto)]
    [InlineData(PoolBackend.LoopXfs)]
    public async Task Too_little_host_space_is_exit_3_with_no_fallback_and_nothing_created(PoolBackend backend)
    {
        _hostFree = 20_000_000_000;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Host().ProvisionAsync(With(backend), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.HostDiskFull, ex.Code);
        Assert.Contains("60 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20 GB", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Volumes);
        Assert.DoesNotContain(Scripts, s => s.Contains("mkfs", StringComparison.Ordinal));
        Assert.DoesNotContain(Scripts, s => s.Contains("chown", StringComparison.Ordinal));
        AssertNothingLeft();
    }

    // The unprivileged helper itself fails

    [Fact]
    public async Task Auto_falls_back_to_copy_mode_with_the_reason_when_the_file_check_helper_cannot_run()
    {
        _failFileCheck = true;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal("plain", pool.Backend);
        Assert.Equal(CloneMode.Copy, pool.Mode);
        Assert.Contains("could not be checked", pool.FallbackReason, StringComparison.Ordinal);
        Assert.Contains("Unable to find image", pool.FallbackReason, StringComparison.Ordinal);
        Assert.DoesNotContain(Scripts, s => s.Contains("mkfs", StringComparison.Ordinal));
        AssertNothingLeft();
    }

    [Fact]
    public async Task Loop_xfs_is_pool_unavailable_exit_3_when_the_file_check_helper_cannot_run()
    {
        _failFileCheck = true;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Host().ProvisionAsync(With(PoolBackend.LoopXfs), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.PoolUnavailable, ex.Code);
        Assert.Equal(ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
        Assert.Contains("Unable to find image", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    // The volume

    [Fact]
    public async Task The_fallback_refuses_a_volume_of_that_name_without_the_managed_label_and_does_not_touch_it()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _refusePrivileged = true;
        _runtime.Volumes.Add("sqlbranch-pool");
        _runtime.UnlabeledVolumes.Add("sqlbranch-pool");

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
        Assert.DoesNotContain(_runtime.Runs, r => r.Mounts.Any(m => m.Source == "sqlbranch-pool"));
        Assert.DoesNotContain(_runtime.Calls, c => c.StartsWith("volume", StringComparison.Ordinal));
        Assert.Contains("sqlbranch-pool", _runtime.Volumes);
    }

    [Fact]
    public async Task The_fallback_creates_the_volume_under_the_name_it_was_given()
    {
        _runtime.Info = new RuntimeInfo("Docker Desktop", "aarch64", "docker-desktop");
        _refusePrivileged = true;
        var provisioner = new PoolProvisioner(_runtime, ManagedPaths.Default, poolVolume: "sqlbranch-it-pool-q1");
        Host();

        var pool = await provisioner.ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal(new PoolLocation(PoolKind.Volume, "sqlbranch-it-pool-q1"), pool.Location);
        Assert.Contains("sqlbranch-it-pool-q1", _runtime.Volumes);
        Assert.DoesNotContain("sqlbranch-pool", _runtime.Volumes);
    }

    // loop-xfs

    [Fact]
    public async Task Loop_xfs_with_a_refused_privileged_container_is_exit_3_naming_the_cause()
    {
        _refusePrivileged = true;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Host().ProvisionAsync(With(PoolBackend.LoopXfs), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.PoolUnavailable, ex.Code);
        Assert.Equal(ExitCode.EnvironmentNotReady, ex.Code.ExitCode);
        Assert.Contains("privileged containers are not allowed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Volumes);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Loop_xfs_with_a_failed_mount_is_exit_3_and_removes_the_file()
    {
        _failMount = true;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Host().ProvisionAsync(With(PoolBackend.LoopXfs), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.PoolUnavailable, ex.Code);
        Assert.Contains("failed to setup loop device", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Loop_xfs_never_touches_the_pool_directory()
    {
        await Host().ProvisionAsync(With(PoolBackend.LoopXfs), "x", CancellationToken.None);

        Assert.DoesNotContain(_runtime.Runs, r => r.Mounts.Any(m => m.Source == PoolDir));
    }

    // existing managed pool

    [Fact]
    public async Task A_changed_pool_size_is_refused_with_exit_2_and_nothing_is_touched()
    {
        _existingFile = 100_000_000_000;
        _mounted = true;
        var settings = With(PoolBackend.Auto) with { Origins = new Dictionary<string, string> { ["poolSize"] = "/repo/.sqlbranch.yml" } };

        var ex = await Assert.ThrowsAsync<ConfigException>(() => Host().ProvisionAsync(settings, "x", CancellationToken.None));

        Assert.Contains("poolSize", ex.Message, StringComparison.Ordinal);
        Assert.Contains("100 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("60 GB", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".sqlbranch.yml", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Scripts, s => s.Contains("mkfs", StringComparison.Ordinal) || s.Contains("rm -f", StringComparison.Ordinal) || s.Contains("mount -o loop", StringComparison.Ordinal));
        Assert.Equal(100_000_000_000, _existingFile);
    }

    [Fact]
    public async Task An_existing_pool_of_the_same_size_is_reused_and_remounted_when_needed()
    {
        _existingFile = Sixty.Bytes;
        _mounted = false;

        var pool = await Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None);

        Assert.Equal("loop-xfs", pool.Backend);
        Assert.False(pool.CreatedManagedPool);
        Assert.True(_mounted);
        Assert.DoesNotContain(Scripts, s => s.Contains("mkfs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_remount_of_an_existing_pool_is_pool_not_mounted()
    {
        _existingFile = Sixty.Bytes;
        _failRemount = true;

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => Host().ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.PoolNotMounted, ex.Code);
        Assert.Equal(Sixty.Bytes, _existingFile);
    }

    [Fact]
    public async Task Docker_being_unavailable_stops_everything_with_exit_3()
    {
        var provisioner = Host();
        var failing = new ThrowingInfoRuntime();
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            new PoolProvisioner(failing, ManagedPaths.Default).ProvisionAsync(With(PoolBackend.Auto), "x", CancellationToken.None));

        Assert.Equal(ErrorCodes.DockerUnavailable, ex.Code);
        Assert.NotNull(provisioner);
    }

    private sealed class ThrowingInfoRuntime : IContainerRuntime
    {
        public Task<RuntimeInfo> GetInfoAsync(CancellationToken cancellationToken) =>
            throw new SqlBranchException(ErrorCodes.DockerUnavailable, "Docker is not reachable.");

        public Task<ContainerResult> RunAsync(ContainerSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ContainerResult> ExecAsync(string container, IReadOnlyList<string> command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IStreamingProcess> ExecStreamAsync(string container, IReadOnlyList<string> command, StreamDirection direction, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CopyToAsync(string container, string hostPath, string containerPath, string owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ContainerInfo?> InspectAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task StartAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task StopAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> LogsAsync(string name, int tail, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ContainerInfo>> ListManagedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> VolumeIsManagedAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateVolumeAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveVolumeAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

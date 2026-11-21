using System.Diagnostics;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

namespace SqlBranch.Core.Tests.Containers;

/// <summary>The streaming exec of design D3: bytes pass as a stream, the exit code is awaited, a cancelled client is killed and awaited.</summary>
public sealed class StreamingExecTests
{
    private const long GiB = 1L << 30;

    /// <summary>A stream that never delivers anything, and ends with <see cref="OperationCanceledException"/> as a killed client's pipe would.</summary>
    private sealed class BlockedStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task One_gibibyte_streams_through_the_fake_runtime_without_being_buffered()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Containers["sqlbranch-engine"] = new ContainerInfo("sqlbranch-engine", "img", true,
            new Dictionary<string, string> { ["io.sqlbranch.managed"] = "true" }, []);
        var source = new SyntheticStream(GiB, fill: 7);
        runtime.StreamHandler = (_, _, _, token) => new FakeStreamingProcess(source, cancellationToken: token);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await using var process = await runtime.ExecStreamAsync("sqlbranch-engine", ["tar", "-cf", "-"], StreamDirection.Read, CancellationToken.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await process.Stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        var result = await process.WaitAsync(CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(GiB, total);
        Assert.Equal(0, result.ExitCode);
        Assert.True(source.LargestRead <= buffer.Length, $"A read asked for {source.LargestRead} bytes.");
        // GetTotalAllocatedBytes counts every thread, so tests running in parallel add to it.
        // A buffered copy would allocate at least 1 GiB; 256 MiB leaves room for that noise.
        Assert.True(allocated < 256L << 20, $"Streaming 1 GiB allocated {allocated} bytes.");
        var exec = Assert.Single(runtime.StreamExecs);
        Assert.Equal("sqlbranch-engine", exec.Container);
        Assert.Equal(["tar", "-cf", "-"], exec.Command);
        Assert.Equal(StreamDirection.Read, exec.Direction);
    }

    [Fact]
    public async Task A_non_zero_exit_and_its_error_text_are_reported()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Containers["sqlbranch-engine"] = new ContainerInfo("sqlbranch-engine", "img", true,
            new Dictionary<string, string> { ["io.sqlbranch.managed"] = "true" }, []);
        runtime.StreamHandler = (_, _, _, _) => new FakeStreamingProcess(new MemoryStream([1, 2, 3]), exitCode: 2, stdErr: "tar: shop.mdf: file changed");

        await using var process = await runtime.ExecStreamAsync("sqlbranch-engine", ["tar"], StreamDirection.Read, CancellationToken.None);
        using var sink = new MemoryStream();
        await process.Stream.CopyToAsync(sink);
        var result = await process.WaitAsync(CancellationToken.None);

        Assert.Equal(3, sink.Length);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("file changed", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_returns_only_after_the_process_has_exited()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Containers["sqlbranch-engine"] = new ContainerInfo("sqlbranch-engine", "img", true,
            new Dictionary<string, string> { ["io.sqlbranch.managed"] = "true" }, []);
        FakeStreamingProcess? fake = null;
        runtime.StreamHandler = (_, _, _, token) => fake = new FakeStreamingProcess(new BlockedStream(), cancellationToken: token)
        {
            ExitDelay = TimeSpan.FromMilliseconds(200),
        };
        using var cancel = new CancellationTokenSource();

        async Task Consume()
        {
            await using var process = await runtime.ExecStreamAsync("sqlbranch-engine", ["tar", "-cf", "-"], StreamDirection.Read, cancel.Token);
            var buffer = new byte[16];
            await process.Stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, cancel.Token);
        }

        var consuming = Consume();
        await Task.Delay(50);
        Assert.False(consuming.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consuming);

        Assert.NotNull(fake);
        Assert.Equal(["killed", "exited"], fake.Events);
    }

    [Fact]
    public async Task The_fake_runtime_refuses_a_container_without_the_label()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Containers["other"] = new ContainerInfo("other", "img", true, new Dictionary<string, string>(), []);

        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            runtime.ExecStreamAsync("other", ["ls"], StreamDirection.Read, CancellationToken.None));

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
    }

    [Fact]
    public async Task The_docker_runtime_builds_the_exec_command_and_refuses_an_unlabeled_container()
    {
        var runner = new FakeProcessRunner();
        runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: true), string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);
        runner.StartHandler = (_, _, token) => new FakeStreamingProcess(Stream.Null, cancellationToken: token);
        var runtime = new DockerRuntime(runner, new SecretRedactor());

        await using (await runtime.ExecStreamAsync("sqlbranch-engine", ["tar", "-xf", "-"], StreamDirection.Write, CancellationToken.None))
        {
        }

        await using (await runtime.ExecStreamAsync("sqlbranch-engine", ["tar", "-cf", "-"], StreamDirection.Read, CancellationToken.None))
        {
        }

        Assert.Equal(["exec", "-i", "sqlbranch-engine", "tar", "-xf", "-"], runner.Starts[0].Args);
        Assert.Equal(StreamDirection.Write, runner.Starts[0].Direction);
        Assert.Equal(["exec", "sqlbranch-engine", "tar", "-cf", "-"], runner.Starts[1].Args);

        runner.Starts.Clear();
        runner.Handler = a => a[0] == "inspect"
            ? new ProcessOutput(0, FakeProcessRunner.InspectJson("sqlbranch-engine", managed: false), string.Empty)
            : new ProcessOutput(0, string.Empty, string.Empty);
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() =>
            runtime.ExecStreamAsync("sqlbranch-engine", ["ls"], StreamDirection.Read, CancellationToken.None));

        Assert.Equal(ErrorCodes.ForeignContainer, ex.Code);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task A_real_process_streams_its_output_reports_its_exit_code_and_its_error_text()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new SystemProcessRunner();
        await using var process = runner.Start("sh", ["-c", "head -c 3000000 /dev/zero; echo oops >&2; exit 3"], null, StreamDirection.Read, CancellationToken.None);
        using var sink = new MemoryStream();
        await process.Stream.CopyToAsync(sink);
        var result = await process.WaitAsync(CancellationToken.None);

        Assert.Equal(3_000_000, sink.Length);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_process_takes_its_input_as_a_stream()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new SystemProcessRunner();
        await using var process = runner.Start("sh", ["-c", "wc -c"], null, StreamDirection.Write, CancellationToken.None);
        var chunk = new byte[1 << 20];
        for (var i = 0; i < 4; i++)
        {
            await process.Stream.WriteAsync(chunk);
        }

        var result = await process.WaitAsync(CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("4194304", result.StdOut.Trim());
    }

    [Fact]
    public async Task Cancelling_a_real_process_kills_it_and_waits_for_its_exit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        var runner = new SystemProcessRunner();
        var process = runner.Start("sh", ["-c", "sleep 60"], null, StreamDirection.Read, cancel.Token);
        var clock = Stopwatch.StartNew();
        var read = process.Stream.ReadAsync(new byte[16]).AsTask();
        await cancel.CancelAsync();

        await process.DisposeAsync();
        await read.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The client was not killed.");
    }
}

using System.Security.Cryptography;
using SqlBranch.Core.Containers;

namespace SqlBranch.IntegrationTests;

/// <summary>Task 2.0: bytes move through <c>docker exec</c> as streams, in both directions, against a real container.</summary>
[Trait("Category", "Integration")]
public sealed class StreamingExecIntegrationTests
{
    private const long Size = 100L << 20;

    [IntegrationFact]
    public async Task A_100_MiB_file_streams_out_of_a_labeled_container_and_back_in_with_equal_hashes()
    {
        var runtime = DockerHost.Runtime();
        var name = $"sqlbranch-it-stream-{DockerHost.NewId()}";
        await runtime.RunAsync(new ContainerSpec
        {
            Name = name,
            Image = DockerHost.Image,
            Detach = true,
            Entrypoint = string.Empty,
            Command = ["sleep", "600"],
        }, CancellationToken.None);
        try
        {
            var made = await runtime.ExecAsync(name,
                ["sh", "-c", $"head -c {Size} /dev/urandom > /tmp/big && sha256sum /tmp/big | cut -d' ' -f1"], CancellationToken.None);
            Assert.True(made.Succeeded, made.StdErr);
            var expected = made.LastLine;

            // Out of the container.
            string actual;
            long count = 0;
            await using (var reader = await runtime.ExecStreamAsync(name, ["cat", "/tmp/big"], StreamDirection.Read, CancellationToken.None))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await reader.Stream.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    count += read;
                }

                var done = await reader.WaitAsync(CancellationToken.None);
                Assert.Equal(0, done.ExitCode);
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            Assert.Equal(Size, count);
            Assert.Equal(expected, actual);

            // Into the container, from the same bytes streamed out again.
            await using (var source = await runtime.ExecStreamAsync(name, ["cat", "/tmp/big"], StreamDirection.Read, CancellationToken.None))
            await using (var sink = await runtime.ExecStreamAsync(name, ["sh", "-c", "cat > /tmp/copy"], StreamDirection.Write, CancellationToken.None))
            {
                await source.Stream.CopyToAsync(sink.Stream);
                var written = await sink.WaitAsync(CancellationToken.None);
                Assert.Equal(0, written.ExitCode);
            }

            var copy = await runtime.ExecAsync(name, ["sh", "-c", "sha256sum /tmp/copy | cut -d' ' -f1"], CancellationToken.None);
            Assert.Equal(expected, copy.LastLine);

            // A failing command reports its exit code and error text.
            await using var failing = await runtime.ExecStreamAsync(name, ["sh", "-c", "echo nope >&2; exit 4"], StreamDirection.Read, CancellationToken.None);
            await failing.Stream.CopyToAsync(Stream.Null);
            var failed = await failing.WaitAsync(CancellationToken.None);
            Assert.Equal(4, failed.ExitCode);
            Assert.Contains("nope", failed.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
        }
    }

    [IntegrationFact]
    public async Task Cancelling_a_stream_kills_the_exec_client_and_leaves_the_container_running()
    {
        var runtime = DockerHost.Runtime();
        var name = $"sqlbranch-it-stream-{DockerHost.NewId()}";
        await runtime.RunAsync(new ContainerSpec
        {
            Name = name,
            Image = DockerHost.Image,
            Detach = true,
            Entrypoint = string.Empty,
            Command = ["sleep", "600"],
        }, CancellationToken.None);
        try
        {
            using var cancel = new CancellationTokenSource();
            var process = await runtime.ExecStreamAsync(name, ["sh", "-c", "sleep 120"], StreamDirection.Read, cancel.Token);
            var pending = process.Stream.ReadAtLeastAsync(new byte[16], 1, throwOnEndOfStream: false).AsTask();
            await Task.Delay(500);
            await cancel.CancelAsync();
            await process.DisposeAsync();

            Assert.Equal(0, await pending.WaitAsync(TimeSpan.FromSeconds(30)));
            var info = await runtime.InspectAsync(name, CancellationToken.None);
            Assert.True(info is { Running: true });
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
        }
    }
}

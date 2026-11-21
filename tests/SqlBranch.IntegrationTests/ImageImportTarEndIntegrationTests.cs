using SqlBranch.Core.Containers;
using SqlBranch.Core.Images;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 3.3 against a real container and the real GNU <c>tar</c>: killing the <c>docker exec</c> client does not reliably stop the
/// <c>tar</c> inside the container, so the import's cleanup must end it itself before it removes anything. A labeled container of the test image
/// stands in for the engine; the import's own command and end-check script run in it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ImageImportTarEndIntegrationTests
{
    private static async Task<string> CountAsync(DockerRuntime runtime, string name, string marker)
    {
        // Counts processes whose command line holds the marker, without counting the shell that looks.
        var result = await runtime.ExecAsync(name,
            ["sh", "-c", "n=0; for p in /proc/[0-9]*; do [ \"$p\" = /proc/$$ ] && continue; if tr '\\0' ' ' < $p/cmdline 2>/dev/null | grep -qF -- \"$1\"; then n=$((n+1)); fi; done; echo $n", "sh", marker],
            CancellationToken.None);
        return result.StdOut.Trim();
    }

    [IntegrationFact]
    public async Task The_end_check_stops_the_imports_tar_inside_the_container_and_only_it()
    {
        var runtime = DockerHost.Runtime();
        var name = $"sqlbranch-it-import-{DockerHost.NewId()}";
        const string id = "it0123456789";
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
            var data = ImagePaths.Build(id) + "/data";
            var made = await runtime.ExecAsync(name, ["sh", "-c", $"mkdir -p {data}"], CancellationToken.None);
            Assert.True(made.Succeeded, made.StdErr);

            // The import's own command, with its standard input held open and never closed: the tar waits for the archive.
            using var cancel = new CancellationTokenSource();
            var process = await runtime.ExecStreamAsync(name, ImageImporter.TarCommand(id), StreamDirection.Write, cancel.Token);
            await process.Stream.WriteAsync(new byte[512]);
            await process.Stream.FlushAsync();
            for (var i = 0; i < 50 && await CountAsync(runtime, name, data) == "0"; i++)
            {
                await Task.Delay(100);
            }

            Assert.Equal("1", await CountAsync(runtime, name, data));

            // The end-check runs against a live tar, as it does after the cleanup killed the client: the docker client is a separate
            // process from the tar, and what happens to the tar when the client dies depends on the Docker version and on what it reads.
            var ended = await runtime.ExecAsync(name, ["sh", "-c", ImageImporter.EnsureEndedScript, "sh", ImageImporter.PidFile(id), data], CancellationToken.None);

            Assert.True(ended.Succeeded, ended.StdErr);
            Assert.Contains("SQLBRANCH_TAR=ended", ended.StdOut, StringComparison.Ordinal);
            Assert.Equal("0", await CountAsync(runtime, name, data));
            var gone = await runtime.ExecAsync(name, ["sh", "-c", $"[ ! -e {ImageImporter.PidFile(id)} ] && sleep 0 && echo gone"], CancellationToken.None);
            Assert.Contains("gone", gone.StdOut, StringComparison.Ordinal);

            // The container's own process is still there: the script stopped the import's tar only.
            Assert.Equal("1", await CountAsync(runtime, name, "sleep 600"));

            // With nothing running it is a no-op that succeeds.
            var again = await runtime.ExecAsync(name, ["sh", "-c", ImageImporter.EnsureEndedScript, "sh", ImageImporter.PidFile(id), data], CancellationToken.None);
            Assert.True(again.Succeeded, again.StdErr);

            // The client of a tar that was stopped under it ends on its own; disposing it kills it if not and waits.
            await process.DisposeAsync();
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
        }
    }

    [IntegrationFact]
    public async Task The_end_check_leaves_a_process_alone_when_the_pid_file_names_something_else()
    {
        var runtime = DockerHost.Runtime();
        var name = $"sqlbranch-it-import-{DockerHost.NewId()}";
        const string id = "it9876543210";
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
            // A recycled process id: the pid file names the container's own sleep, whose command line does not hold the import's folder.
            var wrote = await runtime.ExecAsync(name,
                ["sh", "-c", $"for p in /proc/[0-9]*; do if tr '\\0' ' ' < $p/cmdline | grep -q '^sleep 600'; then echo ${{p#/proc/}} > {ImageImporter.PidFile(id)}; fi; done"],
                CancellationToken.None);
            Assert.True(wrote.Succeeded, wrote.StdErr);

            var result = await runtime.ExecAsync(name,
                ["sh", "-c", ImageImporter.EnsureEndedScript, "sh", ImageImporter.PidFile(id), ImagePaths.Build(id) + "/data"], CancellationToken.None);

            Assert.True(result.Succeeded, result.StdErr);
            Assert.Equal("1", await CountAsync(runtime, name, "sleep 600"));
        }
        finally
        {
            await runtime.RemoveAsync(name, CancellationToken.None);
        }
    }
}

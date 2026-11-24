using System.Diagnostics;
using System.Text;
using SqlBranch.Core;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Output;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;
using SqlBranch.Core.Tests.Images;

namespace SqlBranch.Core.Tests.Engine;

public sealed class EngineCredentialsTests
{
    private const string Password = "Sb#Secret0123";

    private static readonly ProcessOutput NoSuchContainer = new(1, string.Empty, "Error: No such container");

    [Fact]
    public async Task The_stored_password_survives_the_runtimes_redaction_of_container_output()
    {
        // The real runtime masks every registered secret in what a container prints. The file therefore
        // travels as base64, or the password would come back as ***.
        var json = $"{{\"user\":\"sa\",\"password\":\"{Password}\"}}";
        var runner = new FakeProcessRunner
        {
            Handler = args => args[0] == "run"
                ? new ProcessOutput(0, "SQLBRANCH_CREDENTIALS=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n", string.Empty)
                : NoSuchContainer,
        };
        var redactor = new SecretRedactor();
        redactor.Register(Password);
        var runtime = new DockerRuntime(runner, redactor);

        var stored = await EngineCredentials.ReadAsync(runtime, "img", new PoolLocation(PoolKind.Directory, "/data/sb"), CancellationToken.None);

        Assert.Equal(Password, stored);
    }

    [Fact]
    public async Task A_pool_without_a_credentials_file_reads_as_null()
    {
        var runner = new FakeProcessRunner
        {
            Handler = args => args[0] == "run" ? new ProcessOutput(0, "SQLBRANCH_CREDENTIALS=none\n", string.Empty) : NoSuchContainer,
        };
        var runtime = new DockerRuntime(runner, new SecretRedactor());

        Assert.Null(await EngineCredentials.ReadAsync(runtime, "img", new PoolLocation(PoolKind.Directory, "/data/sb"), CancellationToken.None));
    }

    [Fact]
    public async Task Writing_passes_the_password_on_stdin_and_keeps_it_off_the_command_line_and_the_environment()
    {
        var runner = new FakeProcessRunner { Handler = args => args[0] == "run" ? new ProcessOutput(0, string.Empty, string.Empty) : NoSuchContainer };
        var runtime = new DockerRuntime(runner, new SecretRedactor());

        await EngineCredentials.WriteAsync(runtime, "img", new PoolLocation(PoolKind.Directory, "/data/sb"), Password, CancellationToken.None);

        var call = runner.Calls.First(c => c.Args[0] == "run");
        Assert.DoesNotContain(call.Args, a => a.Contains(Password, StringComparison.Ordinal));
        Assert.Contains("-i", call.Args);
        Assert.Contains(Password, runner.Inputs[runner.Calls.IndexOf(call)]!, StringComparison.Ordinal);
        Assert.DoesNotContain(call.Env ?? new Dictionary<string, string>(), e => e.Value.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(call.Args, a => a.Contains("SQLBRANCH_CREDENTIALS_JSON", StringComparison.Ordinal));
    }

    private static readonly PoolLocation Pool = new(PoolKind.Directory, "/data/sb");

    private static DockerRuntime RuntimeReturning(string json)
    {
        var runner = new FakeProcessRunner
        {
            Handler = args => args[0] == "run"
                ? new ProcessOutput(0, "SQLBRANCH_CREDENTIALS=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n", string.Empty)
                : NoSuchContainer,
        };
        return new DockerRuntime(runner, new SecretRedactor());
    }

    [Fact]
    public async Task Format_1_reads_the_top_level_password_and_format_2_the_nested_one()
    {
        var one = await EngineCredentials.ReadFileAsync(RuntimeReturning($"{{\"user\":\"sa\",\"password\":\"{Password}\"}}"), "img", Pool, CancellationToken.None);
        var two = await EngineCredentials.ReadFileAsync(RuntimeReturning(
            $"{{\"format\":2,\"engine\":{{\"user\":\"sa\",\"password\":\"{Password}\"}},\"upgrade\":{{\"startedAt\":\"2026-12-01T09:00:00.000Z\",\"state\":\"running\"}},\"note\":\"n\"}}"),
            "img", Pool, CancellationToken.None);

        Assert.Equal(1, one!.Format);
        Assert.Equal(Password, one.Password);
        Assert.Null(one.Upgrade);
        Assert.Equal(2, two!.Format);
        Assert.Equal(Password, two.Password);
        Assert.Equal("running", two.Upgrade!.State);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero), two.Upgrade.StartedAt);
        Assert.Equal("n", two.Note);
    }

    [Fact]
    public async Task ReadAsync_returns_the_password_of_a_format_2_file()
    {
        var json = CredentialsFile.Format2(Password, new UpgradeRecord(DateTimeOffset.UnixEpoch, UpgradeRecord.Done)).ToJson();

        Assert.Equal(Password, await EngineCredentials.ReadAsync(RuntimeReturning(json), "img", Pool, CancellationToken.None));
    }

    [Fact]
    public async Task A_format_2_file_has_no_top_level_password_so_a_0_2_2_reader_stops()
    {
        var json = CredentialsFile.Format2(Password, new UpgradeRecord(DateTimeOffset.UnixEpoch, UpgradeRecord.Running)).ToJson();
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("password", out _));
        Assert.Equal(2, document.RootElement.GetProperty("format").GetInt32());
        Assert.Equal(Password, document.RootElement.GetProperty("engine").GetProperty("password").GetString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Pool_from_a_newer_release_is_refused_with_operation_failed_and_the_update_hint()
    {
        var ex = await Assert.ThrowsAsync<SqlBranchException>(() => EngineCredentials.ReadAsync(
            RuntimeReturning("{\"format\":3,\"engine\":{\"user\":\"sa\",\"password\":\"x\"}}"), "img", Pool, CancellationToken.None));

        Assert.Equal(ErrorCodes.OperationFailed, ex.Code);
        Assert.Contains("format 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Update the sqlbranch tool and the SqlBranch.Testing package", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_format_2_write_runs_as_root_and_keeps_the_password_off_arguments_and_environment()
    {
        var runner = new FakeProcessRunner { Handler = args => args[0] == "run" ? new ProcessOutput(0, string.Empty, string.Empty) : NoSuchContainer };
        var runtime = new DockerRuntime(runner, new SecretRedactor());

        await EngineCredentials.WriteFileAsync(runtime, "img", Pool, CredentialsFile.Format2(Password, new UpgradeRecord(DateTimeOffset.UnixEpoch, UpgradeRecord.Running)), CancellationToken.None);

        var index = runner.Calls.FindIndex(c => c.Args[0] == "run");
        var call = runner.Calls[index];
        Assert.DoesNotContain(call.Args, a => a.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(call.Env ?? new Dictionary<string, string>(), e => e.Value.Contains(Password, StringComparison.Ordinal));
        Assert.All(runner.Calls, c => Assert.DoesNotContain(c.Args, a => a.Contains(Password, StringComparison.Ordinal)));
        Assert.Equal("0", call.Args[call.Args.ToList().IndexOf("--user") + 1]);
        Assert.Contains("-i", call.Args);
        Assert.Contains(Password, runner.Inputs[index]!, StringComparison.Ordinal);
        var script = call.Args[^1];
        Assert.Contains("credentials.json.new", script, StringComparison.Ordinal);
        Assert.Contains("chown 10001:0", script, StringComparison.Ordinal);
        Assert.Contains("mv -f", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_format_1_write_is_still_the_0_2_2_write_with_no_temporary_file()
    {
        var runner = new FakeProcessRunner { Handler = args => args[0] == "run" ? new ProcessOutput(0, string.Empty, string.Empty) : NoSuchContainer };

        await EngineCredentials.WriteAsync(new DockerRuntime(runner, new SecretRedactor()), "img", Pool, Password, CancellationToken.None);

        var call = runner.Calls.First(c => c.Args[0] == "run");
        Assert.DoesNotContain("credentials.json.new", call.Args[^1], StringComparison.Ordinal);
        Assert.Contains("\"user\":\"sa\"", runner.Inputs[runner.Calls.IndexOf(call)]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_pool_in_the_write_is_pool_full_and_a_failed_write_is_operation_failed()
    {
        var full = new FakeProcessRunner { Handler = args => args[0] == "run" ? new ProcessOutput(1, string.Empty, "cat: write error: No space left on device") : NoSuchContainer };
        var other = new FakeProcessRunner { Handler = args => args[0] == "run" ? new ProcessOutput(1, string.Empty, "chown: failed") : NoSuchContainer };
        var file = CredentialsFile.Format2(Password, null);

        var a = await Assert.ThrowsAsync<SqlBranchException>(() => EngineCredentials.WriteFileAsync(new DockerRuntime(full, new SecretRedactor()), "img", Pool, file, CancellationToken.None));
        var b = await Assert.ThrowsAsync<SqlBranchException>(() => EngineCredentials.WriteFileAsync(new DockerRuntime(other, new SecretRedactor()), "img", Pool, file, CancellationToken.None));

        Assert.Equal(ErrorCodes.PoolFull, a.Code);
        Assert.Equal(ErrorCodes.OperationFailed, b.Code);
        Assert.DoesNotContain(Password, a.Message + b.Message, StringComparison.Ordinal);
    }

    private const string OldContent = "{\"user\":\"sa\",\"password\":\"Old#Secret1\"}";

    /// <summary>Runs the real rewrite script in a temporary folder, with one command replaced by a shim that fails.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static (int Exit, string Directory, string[] Files) RunRewrite(string? failing)
    {
        var root = Directory.CreateTempSubdirectory("sqlbranch-creds-").FullName;
        var engine = Path.Combine(root, "engine");
        var shims = Path.Combine(root, "shims");
        Directory.CreateDirectory(engine);
        Directory.CreateDirectory(shims);
        File.WriteAllText(Path.Combine(engine, "credentials.json"), OldContent);

        // chown to 10001 needs root; sync of a path is not needed for the test. Every shim succeeds unless it is the failing one.
        foreach (var command in new[] { "chown", "sync", "chmod", "mv", "cat" })
        {
            var real = command is "chown" or "sync" ? string.Empty : $"exec /bin/{command} \"$@\"";
            var body = command == failing ? "exit 1" : command is "chown" or "sync" ? "exit 0" : real;
            var shim = Path.Combine(shims, command);
            File.WriteAllText(shim, $"#!/bin/sh\n{body}\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var script = EngineCredentials.RewriteScript(engine);
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.Environment["PATH"] = shims + ":/bin:/usr/bin";
        using var process = Process.Start(start)!;
        process.StandardInput.Write(CredentialsFile.Format2("New#Secret2", new UpgradeRecord(DateTimeOffset.UnixEpoch, UpgradeRecord.Running)).ToJson());
        process.StandardInput.Close();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, engine, [.. Directory.GetFiles(engine).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]);
    }

    [UnixTheory]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    [InlineData("cat")]
    [InlineData("chown")]
    [InlineData("chmod")]
    [InlineData("sync")]
    [InlineData("mv")]
    public void A_fault_at_each_step_before_the_rename_leaves_the_old_file_byte_equal_and_no_temporary_file(string failing)
    {
        var (exit, directory, files) = RunRewrite(failing);
        try
        {
            Assert.Equal(1, exit);
            Assert.Equal(["credentials.json"], files);
            Assert.Equal(OldContent, File.ReadAllText(Path.Combine(directory, "credentials.json")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_write_with_no_fault_replaces_the_file_with_mode_600_and_leaves_no_temporary_file()
    {
        var (exit, directory, files) = RunRewrite(null);
        try
        {
            Assert.Equal(0, exit);
            Assert.Equal(["credentials.json"], files);
            var text = File.ReadAllText(Path.Combine(directory, "credentials.json"));
            Assert.Contains("New#Secret2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Old#Secret1", text, StringComparison.Ordinal);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "credentials.json")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }
}

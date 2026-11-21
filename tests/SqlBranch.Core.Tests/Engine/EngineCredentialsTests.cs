using System.Text;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;
using SqlBranch.Core.Tests.Fakes;

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
}

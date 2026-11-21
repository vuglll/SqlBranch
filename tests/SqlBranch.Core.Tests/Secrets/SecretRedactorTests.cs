using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Tests.Secrets;

public class SecretRedactorTests
{
    [Fact]
    public void Masks_every_occurrence()
    {
        var redactor = new SecretRedactor();
        redactor.Register("p@ss");

        Assert.Equal("a *** b ***", redactor.Redact("a p@ss b p@ss"));
    }

    [Fact]
    public void Masks_a_docker_command_line()
    {
        var redactor = new SecretRedactor();
        redactor.Register("Xy9!long-secret");

        var line = redactor.Redact("docker run -e MSSQL_SA_PASSWORD=Xy9!long-secret mcr.microsoft.com/mssql/server:2022-latest");

        Assert.Equal("docker run -e MSSQL_SA_PASSWORD=*** mcr.microsoft.com/mssql/server:2022-latest", line);
    }

    [Fact]
    public void Masks_an_engine_log_excerpt_over_several_lines()
    {
        var redactor = new SecretRedactor();
        redactor.Register("Xy9!long-secret");

        var log = "line one\nLogin failed for 'sa' with Xy9!long-secret\nline three";

        Assert.DoesNotContain("Xy9!long-secret", redactor.Redact(log), StringComparison.Ordinal);
    }

    [Fact]
    public void A_longer_secret_containing_a_shorter_one_is_masked_whole()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abc");
        redactor.Register("abcdef");

        Assert.Equal("x *** y", redactor.Redact("x abcdef y"));
    }

    [Fact]
    public void Empty_and_null_are_safe()
    {
        var redactor = new SecretRedactor();
        redactor.Register(null);
        redactor.Register(string.Empty);

        Assert.Equal("text", redactor.Redact("text"));
        Assert.Equal(string.Empty, redactor.Redact(null));
    }
}

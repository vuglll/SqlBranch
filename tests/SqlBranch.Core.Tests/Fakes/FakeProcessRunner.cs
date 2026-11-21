using SqlBranch.Core.Containers;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>Records docker command lines and answers them from a script.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env)> Calls { get; } = [];

    public List<CancellationToken> CancellationTokens { get; } = [];

    /// <summary>The text given on standard input, one entry per call (null when none).</summary>
    public List<string?> Inputs { get; } = [];

    public Func<IReadOnlyList<string>, ProcessOutput> Handler { get; set; } = _ => new ProcessOutput(0, string.Empty, string.Empty);

    public Exception? Throw { get; set; }

    public Task<ProcessOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        Calls.Add((fileName, arguments, environment));
        CancellationTokens.Add(cancellationToken);
        Inputs.Add(standardInput);
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(Handler(arguments));
    }

    /// <summary>Every streaming start: program, arguments, direction.</summary>
    public List<(string File, IReadOnlyList<string> Args, StreamDirection Direction)> Starts { get; } = [];

    public Func<IReadOnlyList<string>, StreamDirection, CancellationToken, IStreamingProcess>? StartHandler { get; set; }

    public IStreamingProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        StreamDirection direction,
        CancellationToken cancellationToken)
    {
        Starts.Add((fileName, arguments, direction));
        return (StartHandler ?? throw new NotSupportedException("No streaming handler was set."))(arguments, direction, cancellationToken);
    }

    public IEnumerable<string> Lines => Calls.Select(c => string.Join(' ', c.Args));

    /// <summary>The <c>docker inspect</c> JSON for one container.</summary>
    public static string InspectJson(string name, bool managed, bool running = true, string image = "img")
    {
        var labels = managed ? "{\"io.sqlbranch.managed\":\"true\"}" : "{}";
        var state = running ? "true" : "false";
        return "[{\"Name\":\"/" + name + "\",\"State\":{\"Running\":" + state + "},"
            + "\"Config\":{\"Image\":\"" + image + "\",\"Labels\":" + labels + "},"
            + "\"NetworkSettings\":{\"Ports\":{\"1433/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"14330\"}]}}}]";
    }
}

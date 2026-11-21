using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using SqlBranch.Cli.Commands;
using SqlBranch.Core;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Output;
using SqlBranch.Core.Secrets;
using CommandResult = SqlBranch.Core.Output.CommandResult;
using ParsedCommand = System.CommandLine.Parsing.CommandResult;

namespace SqlBranch.Cli;

/// <summary>
/// Builds the command tree and runs one command line. Every command returns a
/// <see cref="CommandResult"/>; only the renderer writes output, and no command
/// reads standard input.
/// </summary>
internal sealed class CliApp
{
    private readonly ResultRenderer _renderer;
    private readonly SecretRedactor _redactor;
    private TextWriter _stdout = TextWriter.Null;
    private TextWriter _stderr = TextWriter.Null;
    private Stopwatch _clock = new();

    public CliApp(SecretRedactor redactor)
    {
        _redactor = redactor;
        _renderer = new ResultRenderer(redactor);

        Root = new RootCommand("SqlBranch: writable, full-size SQL Server database branches in seconds.");
        Root.Options.Add(FormatOption);
        Root.Options.Add(QuietOption);
        Root.Options.Add(ConfigOption);
        Root.SetAction(parse => Execute(parse, () => CommandResult.Failure("sqlbranch", ErrorCodes.UsageError,
            "No command given. Run 'sqlbranch --help' for the list of commands.")));
    }

    /// <summary>The app with every real command registered. Program.cs uses this.</summary>
    public static CliApp CreateDefault(SecretRedactor redactor, CliServices? services = null)
    {
        CliServices GetServices() => services ?? CliServices.Real(redactor);
        var app = new CliApp(redactor);
        InitCommand.Register(app, GetServices, redactor);
        DoctorCommand.Register(app, GetServices, redactor);
        StatusCommand.Register(app, GetServices, redactor);
        EngineCommands.Register(app, GetServices, redactor);
        TeardownCommand.Register(app, GetServices, redactor);
        ImageCommands.Register(app, GetServices, redactor);
        BranchCommands.Register(app, GetServices, redactor);
        SkillCommand.Register(app, services?.WorkingDirectory ?? Environment.CurrentDirectory);
        return app;
    }

    /// <summary>--format text|json, on every command.</summary>
    public static Option<OutputFormat> FormatOption { get; } = new("--format")
    {
        Description = "Output format: text or json.",
        Recursive = true,
        DefaultValueFactory = _ => OutputFormat.Text,
        HelpName = "text|json",
    };

    /// <summary>--quiet, on every command.</summary>
    public static Option<bool> QuietOption { get; } = new("--quiet", "-q")
    {
        Description = "Print only the essential result.",
        Recursive = true,
    };

    /// <summary>--config, on every command.</summary>
    public static Option<FileInfo?> ConfigOption { get; } = new("--config")
    {
        Description = "Use this file instead of the repository's .sqlbranch.yml.",
        Recursive = true,
    };

    public RootCommand Root { get; }

    /// <summary>Writes one progress line to standard error while a command runs. Secrets are masked.</summary>
    public void WriteProgress(string line) => _stderr.WriteLine(_redactor.Redact(line));

    /// <summary>The commands added with <see cref="AddCommand"/>, with the sample arguments tests run them with.</summary>
    public IReadOnlyList<RegisteredCommand> Registered => _registered;

    private readonly List<RegisteredCommand> _registered = [];

    /// <summary>
    /// Adds a command whose handler returns a result. The handler gets the parse
    /// result and nothing that can read input.
    /// </summary>
    public Command AddCommand(
        Command parent,
        Command command,
        CommandMetadata metadata,
        Func<ParseResult, CommandResult> handler,
        IReadOnlyList<string>? sampleArgs = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);

        command.SetAction(parse => Execute(parse, () => handler(parse)));
        parent.Subcommands.Add(command);
        _registered.Add(new RegisteredCommand(command, PathOf(command, parent), metadata, sampleArgs ?? []));
        return command;
    }

    private static string[] PathOf(Command command, Command parent)
    {
        var names = new List<string> { command.Name };
        for (var node = parent; node is not null and not RootCommand; node = node.Parents.OfType<Command>().FirstOrDefault())
        {
            names.Insert(0, node.Name);
        }

        return [.. names];
    }

    /// <summary>Runs one command line and returns the process exit code.</summary>
    public int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        _stdout = stdout;
        _stderr = stderr;
        _clock = Stopwatch.StartNew();

        var logicalSource = args.Any(a =>
            a.Equals("--from-bacpac", StringComparison.Ordinal) ||
            a.StartsWith("--from-bacpac=", StringComparison.Ordinal) ||
            a.StartsWith("--from-bacpac:", StringComparison.Ordinal));
        // A pathname beginning with @ is still a source file. Reading it as parser input
        // would inspect package bytes before initialization and could echo their contents.
        var parse = Root.Parse(args, logicalSource ? new ParserConfiguration { ResponseFileTokenReplacer = null } : null);
        // --help and --version always print text; --format does not apply to them.
        if (parse.Errors.Count > 0 || parse.UnmatchedTokens.Count > 0)
        {
            if (SecretFlagVariable(args) is { } variable)
            {
                // The parser's own message would echo the secret's value, so none of it is shown.
                var refusal = CommandResult.Failure(CommandPath(parse), ErrorCodes.UsageError,
                    $"Storage secrets are not accepted as flags. Set {variable} in the environment instead.");
                return _renderer.Render(refusal, DetectFormat(args), quiet: false, _clock.ElapsedMilliseconds, stdout, stderr);
            }

            if (logicalSource && CommandPath(parse) == "image build")
            {
                var refusal = CommandResult.Failure("image build", ErrorCodes.UsageError,
                    "Invalid BACPAC build arguments. Specify exactly one of --from-bak, --from-scripts and --from-bacpac. Run 'sqlbranch image build --help' for usage.");
                return _renderer.Render(refusal, DetectFormat(args), quiet: false, _clock.ElapsedMilliseconds, stdout, stderr);
            }

            var problems = parse.Errors.Select(e => e.Message).Where(m => !m.StartsWith(UnrecognizedPrefix, StringComparison.Ordinal))
                .Concat(DescribeUnmatched(args, parse.UnmatchedTokens))
                .Distinct(StringComparer.Ordinal);
            var message = string.Join(" ", problems) + " Run 'sqlbranch --help' for usage.";
            var failure = CommandResult.Failure(CommandPath(parse), ErrorCodes.UsageError, message);
            return _renderer.Render(failure, DetectFormat(args), quiet: false, _clock.ElapsedMilliseconds, stdout, stderr);
        }

        // The built-in version option reports the entry assembly, which is not
        // SqlBranch under a test host. Report SqlBranch's own version instead.
        var version = Root.Options.OfType<VersionOption>().FirstOrDefault();
        if (version is not null && parse.GetResult(version) is not null)
        {
            stdout.WriteLine(ProductInfo.Version);
            return (int)ExitCode.Success;
        }

        return parse.Invoke(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
            EnableDefaultExceptionHandler = false,
        });
    }

    private const string UnrecognizedPrefix = "Unrecognized command or argument";

    /// <summary>
    /// Names what the parser did not recognize. An unrecognized option is named without any <c>=value</c>, and the token after it
    /// (which may be a secret given under a name this program does not know, in any spelling) is never echoed.
    /// </summary>
    private static IEnumerable<string> DescribeUnmatched(IReadOnlyList<string> args, IReadOnlyList<string> unmatched)
    {
        var cursor = 0;
        var hiddenAt = -1;
        foreach (var token in unmatched)
        {
            var at = -1;
            for (var i = cursor; i < args.Count; i++)
            {
                if (args[i] == token)
                {
                    at = i;
                    break;
                }
            }

            if (at >= 0)
            {
                cursor = at + 1;
            }

            // The token right after an unrecognized option is never echoed, even when it starts with '-'.
            if (at >= 0 && at == hiddenAt)
            {
                continue;
            }

            if (token.StartsWith('-'))
            {
                var equals = token.IndexOfAny(['=', ':']);
                if (at >= 0)
                {
                    hiddenAt = at + 1;
                }

                yield return $"Unrecognized option '{(equals > 0 ? token[..equals] : token)}'.";
            }
            else
            {
                yield return $"Unrecognized command or argument '{token}'.";
            }
        }
    }

    private static readonly (string Flag, string Variable)[] SecretFlags =
    [
        ("--sas-token", "SQLBRANCH_AZURE_SAS_TOKEN"),
        ("--s3-access-key-id", "SQLBRANCH_S3_ACCESS_KEY_ID"),
        ("--s3-secret-access-key", "SQLBRANCH_S3_SECRET_ACCESS_KEY"),
    ];

    /// <summary>The environment variable for a storage secret given as a flag, in either <c>--flag value</c> or <c>--flag=value</c> form.</summary>
    private static string? SecretFlagVariable(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            foreach (var (flag, variable) in SecretFlags)
            {
                if (arg.Equals(flag, StringComparison.Ordinal) || arg.StartsWith(flag + "=", StringComparison.Ordinal))
                {
                    return variable;
                }
            }
        }

        return null;
    }

    private int Execute(ParseResult parse, Func<CommandResult> handler)
    {
        var format = parse.GetValue(FormatOption);
        var quiet = parse.GetValue(QuietOption);

        CommandResult result;
        try
        {
            result = handler();
        }
        catch (ConfigException ex)
        {
            result = CommandResult.Failure(CommandPath(parse), ErrorCodes.ConfigError, _redactor.Redact(ex.Message));
        }
        catch (SqlBranchException ex)
        {
            result = CommandResult.Failure(CommandPath(parse), ex.Code, _redactor.Redact(ex.Message));
        }
#pragma warning disable CA1031 // Every failure must become a result with an exit code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = CommandResult.Failure(CommandPath(parse), ErrorCodes.OperationFailed, _redactor.Redact(ex.Message));
        }

        return _renderer.Render(result, format, quiet, _clock.ElapsedMilliseconds, _stdout, _stderr);
    }

    /// <summary>The command path without the root, for example "branch create".</summary>
    internal static string CommandPath(ParseResult parse)
    {
        var names = new List<string>();
        for (SymbolResult? node = parse.CommandResult; node is ParsedCommand command; node = node.Parent)
        {
            if (command.Command is not RootCommand)
            {
                names.Insert(0, command.Command.Name);
            }
        }

        return names.Count == 0 ? "sqlbranch" : string.Join(" ", names);
    }

    /// <summary>Finds --format json even when the rest of the line does not parse.</summary>
    internal static OutputFormat DetectFormat(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;
            if (arg == "--format" && i + 1 < args.Count)
            {
                value = args[i + 1];
            }
            else if (arg.StartsWith("--format=", StringComparison.Ordinal) || arg.StartsWith("--format:", StringComparison.Ordinal))
            {
                value = arg["--format=".Length..];
            }

            if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
            {
                return OutputFormat.Json;
            }
        }

        return OutputFormat.Text;
    }
}

/// <summary>A command added with <see cref="CliApp.AddCommand"/>.</summary>
/// <param name="Command">The command.</param>
/// <param name="Path">Names from the root, for example <c>branch create</c>.</param>
/// <param name="Metadata">Guidance exported with the agent skill.</param>
/// <param name="SampleArgs">Arguments after the path that make a valid command line, used by tests.</param>
internal sealed record RegisteredCommand(Command Command, IReadOnlyList<string> Path, CommandMetadata Metadata, IReadOnlyList<string> SampleArgs);

internal sealed record CommandMetadata
{
    public required string Purpose { get; init; }
    public required string WhenToUse { get; init; }
    public required string Example { get; init; }
    public required string Destroys { get; init; }
}

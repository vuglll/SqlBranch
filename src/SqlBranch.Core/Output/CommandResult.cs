using System.Text.Json.Nodes;

namespace SqlBranch.Core.Output;

/// <summary>A failure: a stable code and a human-readable message.</summary>
public sealed record CommandError(ErrorCode Code, string Message);

/// <summary>
/// What a command produced. Renderers turn it into text or JSON; nothing else
/// writes to the console.
/// </summary>
public sealed class CommandResult
{
    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.Ordinal) { "schemaVersion", "command", "ok", "elapsedMs", "error" };

    private CommandResult(string command, CommandError? error, JsonObject payload,
        IReadOnlyList<string> textLines, IReadOnlyList<string>? quietLines, IReadOnlyList<string> warnings)
    {
        foreach (var key in payload.Select(p => p.Key))
        {
            if (ReservedKeys.Contains(key))
            {
                throw new ArgumentException($"Payload key '{key}' is reserved for the JSON envelope.", nameof(payload));
            }
        }

        Command = command;
        Error = error;
        Payload = payload;
        TextLines = textLines;
        QuietLines = quietLines ?? textLines;
        Warnings = warnings;
    }

    /// <summary>The command path, for example "branch create".</summary>
    public string Command { get; }

    /// <summary>The failure, or null on success.</summary>
    public CommandError? Error { get; }

    /// <summary>True when the command succeeded.</summary>
    public bool Ok => Error is null;

    /// <summary>Named result objects, merged into the top level of the JSON document.</summary>
    public JsonObject Payload { get; }

    /// <summary>Lines printed to standard output in text format.</summary>
    public IReadOnlyList<string> TextLines { get; }

    /// <summary>The essential lines printed with --quiet.</summary>
    public IReadOnlyList<string> QuietLines { get; }

    /// <summary>Warnings, always printed to standard error.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The process exit code for this result.</summary>
    public ExitCode ExitCode => Error?.Code.ExitCode ?? ExitCode.Success;

    /// <summary>A successful result.</summary>
    public static CommandResult Success(string command, JsonObject? payload = null,
        IReadOnlyList<string>? textLines = null, IReadOnlyList<string>? quietLines = null,
        IReadOnlyList<string>? warnings = null) =>
        new(command, null, payload ?? [], textLines ?? [], quietLines, warnings ?? []);

    /// <summary>
    /// A failed result. <paramref name="textLines"/> is a report that still
    /// belongs on standard output, such as <c>status</c> listing what it found
    /// before exiting 1; the error message goes to standard error.
    /// </summary>
    public static CommandResult Failure(string command, ErrorCode code, string message,
        JsonObject? payload = null, IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? textLines = null) =>
        new(command, new CommandError(code, message), payload ?? [], textLines ?? [], null, warnings ?? []);
}

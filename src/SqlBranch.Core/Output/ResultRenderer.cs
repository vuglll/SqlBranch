using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Output;

/// <summary>
/// Prints a <see cref="CommandResult"/>. Results go to standard output;
/// progress, warnings and error text go to standard error.
/// </summary>
public sealed class ResultRenderer(SecretRedactor redactor)
{
    /// <summary>The JSON contract version.</summary>
    public const int SchemaVersion = 1;

    // Output goes to terminals, files and CI logs, never into HTML, so quotes and
    // apostrophes are written as they are rather than as \u0027.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes the result and returns the exit code to use.</summary>
    public int Render(CommandResult result, OutputFormat format, bool quiet, long elapsedMs,
        TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        foreach (var warning in result.Warnings)
        {
            stderr.WriteLine($"warning: {redactor.Redact(warning)}");
        }

        if (format == OutputFormat.Json)
        {
            stdout.WriteLine(ToJson(result, elapsedMs));
        }
        else if (result.Error is { } error)
        {
            foreach (var line in result.TextLines)
            {
                stdout.WriteLine(redactor.Redact(line));
            }

            stderr.WriteLine($"error: {redactor.Redact(error.Message)}");
        }
        else
        {
            foreach (var line in quiet ? result.QuietLines : result.TextLines)
            {
                stdout.WriteLine(line);
            }
        }

        return (int)result.ExitCode;
    }

    /// <summary>Builds the JSON document for a result.</summary>
    public string ToJson(CommandResult result, long elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(result);

        var document = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["command"] = result.Command,
            ["ok"] = result.Ok,
            ["elapsedMs"] = elapsedMs,
        };

        foreach (var (key, value) in result.Payload)
        {
            document[key] = value?.DeepClone();
        }

        if (result.Error is { } error)
        {
            document["error"] = new JsonObject
            {
                ["code"] = error.Code.Code,
                ["message"] = redactor.Redact(error.Message),
            };
        }

        return document.ToJsonString(JsonOptions);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SqlBranch.Core.Output;

namespace SqlBranch.Core.Images;

public sealed record ScriptFile(string Name, string Sha256)
{
    /// <summary>Fields of this entry that this version does not know, kept as read so an import and a re-export do not lose them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownFields { get; init; }
}
public sealed record ScriptBatch(int Line, string Sql);

public sealed partial record ImageScript(ScriptFile File, IReadOnlyList<ScriptBatch> Batches)
{
    public static ImageScript Read(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            var file = new ScriptFile(Path.GetFileName(path), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            var batches = new List<ScriptBatch>();
            var batch = new StringBuilder();
            var lineNumber = 0;
            var start = 1;
            void Finish()
            {
                var sql = batch.ToString();
                if (!string.IsNullOrWhiteSpace(sql))
                {
                    batches.Add(new ScriptBatch(start, sql));
                }
                batch.Clear();
            }

            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (GoCount().IsMatch(line))
                {
                    throw new SqlBranchException(ErrorCodes.UsageError, $"{file.Name}, line {lineNumber}: GO with a count is unsupported.");
                }
                if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    Finish();
                    start = lineNumber + 1;
                }
                else
                {
                    batch.AppendLine(line);
                }
            }

            Finish();
            return new ImageScript(file, batches);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"Cannot read script '{path}': {ex.Message}");
        }
    }

    public static IReadOnlyList<ImageScript> ReadFolder(string path)
    {
        try
        {
            var scripts = Directory.EnumerateFiles(path).Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(Read).ToArray();
            return scripts.Length > 0 ? scripts : throw new SqlBranchException(ErrorCodes.UsageError, $"No .sql files in '{path}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlBranchException(ErrorCodes.UsageError, $"Cannot read script folder '{path}': {ex.Message}");
        }
    }

    [GeneratedRegex(@"^\s*GO\s+[0-9]+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoCount();
}

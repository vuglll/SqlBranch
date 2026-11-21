using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlBranch.Core.Configuration;

/// <summary>
/// A size in bytes, written like <c>60GB</c> or <c>512MiB</c>. KB, MB, GB and TB
/// are powers of 1000; KiB, MiB, GiB and TiB are powers of 1024.
/// </summary>
public readonly partial record struct ByteSize(long Bytes)
{
    private static readonly Dictionary<string, long> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1,
        ["KB"] = 1_000,
        ["MB"] = 1_000_000,
        ["GB"] = 1_000_000_000,
        ["TB"] = 1_000_000_000_000,
        ["KiB"] = 1L << 10,
        ["MiB"] = 1L << 20,
        ["GiB"] = 1L << 30,
        ["TiB"] = 1L << 40,
    };

    [GeneratedRegex(@"^\s*(?<n>\d+(\.\d+)?)\s*(?<u>[A-Za-z]+)\s*$")]
    private static partial Regex Pattern();

    /// <summary>Parses a size. Returns false for anything that is not a positive number with a unit.</summary>
    public static bool TryParse(string? text, out ByteSize size)
    {
        size = default;
        var match = Pattern().Match(text ?? string.Empty);
        if (!match.Success || !Units.TryGetValue(match.Groups["u"].Value, out var unit))
        {
            return false;
        }

        if (!decimal.TryParse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        try
        {
            var bytes = checked(number * unit);
            if (bytes < 1 || bytes > long.MaxValue)
            {
                return false;
            }

            size = new ByteSize((long)bytes);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>The size in decimal gigabytes, for messages.</summary>
    public string ToGigabytes() =>
        (Bytes / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + " GB";

    /// <summary>The size in the largest binary unit that keeps it at 1 or more, for messages: <c>20 GiB</c>, <c>1.5 MiB</c>.</summary>
    public string ToBinary()
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (decimal)Bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <inheritdoc />
    public override string ToString() => ToGigabytes();
}

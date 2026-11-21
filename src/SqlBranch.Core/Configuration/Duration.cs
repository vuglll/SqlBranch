using System.Globalization;

namespace SqlBranch.Core.Configuration;

public static class Duration
{
    public static TimeSpan Maximum { get; } = TimeSpan.FromDays(365);

    public static TimeSpan? Parse(string value)
    {
        if (value.Length < 2 || !int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            return null;
        }

        var minutes = value[^1] switch { 'm' => 1, 'h' => 60, 'd' => 1440, _ => 0 };
        return minutes > 0 && count <= (int)Maximum.TotalMinutes / minutes ? TimeSpan.FromMinutes(count * minutes) : null;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Purge;

/// <summary>Which timestamp a file's age is measured from.</summary>
public enum AgeBy
{
    /// <summary>Last-modified time (the default).</summary>
    Modified,

    /// <summary>Creation time. Note that copying a file gives the copy a new creation time.</summary>
    Created,
}

/// <summary>
/// Narrows which matching files Purge looks at. Files outside the size range are ignored
/// completely, as if they did not match the pattern: they are never purged and do not
/// count toward keep-number.
/// </summary>
public sealed record FileSelection(AgeBy AgeBy = AgeBy.Modified, long? MinSize = null, long? MaxSize = null)
{
    public static readonly FileSelection Default = new();

    public bool Includes(FileInfo file) =>
        (MinSize is not { } min || file.Length >= min) &&
        (MaxSize is not { } max || file.Length <= max);
}

/// <summary>Parses and formats sizes such as 500, 10KB, 1.5MB, 2GB (1 KB = 1024 bytes).</summary>
public static partial class Size
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([KMGT]?B?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        var match = SizePattern().Match(text ?? "");
        if (!match.Success)
            return false;

        // "", "B" -> bytes; "K"/"KB" -> 1024; "M"/"MB" -> 1024^2; and so on.
        string unit = match.Groups[2].Value.ToUpperInvariant();
        int power = unit.Length == 0 ? 0 : "BKMGT".IndexOf(unit[0]);

        double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * Math.Pow(1024, power);
        if (value > long.MaxValue)
            return false;

        bytes = (long)Math.Round(value);
        return true;
    }

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString("0.#", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}

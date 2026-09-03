using System.Globalization;
using System.Text.RegularExpressions;

namespace KupReport.Reporting;

/// <summary>Extracts [KUP:x] hour tags from PR text.</summary>
public static partial class KupParser
{
    [GeneratedRegex(@"\[\s*KUP\s*:\s*(\d+(?:\.\d)?)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex KupTag();

    /// <summary>Sums all [KUP:x] tags found in the text; null when none present.</summary>
    public static decimal? ExtractHours(string text)
    {
        decimal? total = null;
        foreach (Match match in KupTag().Matches(text))
        {
            var value = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            total = (total ?? 0m) + value;
        }
        return total;
    }
}

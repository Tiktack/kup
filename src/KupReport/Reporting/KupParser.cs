using System.Globalization;
using System.Text.RegularExpressions;

namespace KupReport.Reporting;

/// <summary>Extracts [KUP:x] hour tags from PR text.</summary>
public static partial class KupParser
{
    [GeneratedRegex(@"\[\s*KUP\s*:\s*(\d+(?:\.\d)?)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex KupTag();

    // A tag together with the spaces/tabs before it, so removal leaves no gap.
    [GeneratedRegex(@"[ \t]*\[\s*KUP\s*:\s*\d+(?:\.\d)?\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex KupTagWithLeadingSpace();

    /// <summary>Whether the text contains at least one [KUP:x] tag.</summary>
    public static bool HasTag(string? text) => text is not null && KupTag().IsMatch(text);

    /// <summary>Formats hours as a tag, e.g. <c>[KUP:2.5]</c>.</summary>
    public static string FormatTag(decimal hours) =>
        $"[KUP:{hours.ToString("0.#", CultureInfo.InvariantCulture)}]";

    /// <summary>Removes every [KUP:x] tag from the text and trims trailing whitespace.</summary>
    public static string RemoveTags(string? text) =>
        KupTagWithLeadingSpace().Replace(text ?? "", "").TrimEnd();

    /// <summary>Appends a [KUP:x] tag to a PR description as its own paragraph.</summary>
    public static string AppendTag(string? body, decimal hours)
    {
        var tag = FormatTag(hours);
        var trimmed = (body ?? "").TrimEnd();
        if (trimmed.Length == 0)
            return tag;

        var newLine = trimmed.Contains("\r\n") ? "\r\n" : "\n";
        return $"{trimmed}{newLine}{newLine}{tag}";
    }

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

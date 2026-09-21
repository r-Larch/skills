using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkState.Import.Markdown;

/// <summary>
/// Cell and heading text reduced to something matchable. The corpus writes the same column as
/// <c>Verdict</c>, <c>**Verdict**</c> and <c>verdict?</c>, so every match runs through here first.
/// </summary>
public static partial class MdText
{
    /// <summary>Lower-case, emphasis and code marks removed, whitespace collapsed.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var plain = Plain(value).ToLowerInvariant();
        var kept = new StringBuilder(plain.Length);

        foreach (var c in plain)
        {
            if (char.IsLetterOrDigit(c))
            {
                kept.Append(c);
            }
            else if (c is ' ' or '\t' or '-' or '_' or '/')
            {
                kept.Append(' ');
            }
        }

        return WhitespaceRegex().Replace(kept.ToString(), " ").Trim();
    }

    /// <summary>The cell as a human reads it: emphasis, strikethrough and code fences stripped.</summary>
    public static string Plain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase);

        text = LinkRegex().Replace(text, "$1");
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    public static bool IsStruckThrough(string? value)
        => value is not null && value.Contains("~~", StringComparison.Ordinal);

    /// <summary>True for the corpus's several spellings of "nothing here": —, –, -, n/a, none, empty.</summary>
    public static bool IsEmpty(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0
            || normalized is "n a" or "na" or "none" or "nil" or "tbd" or "pending";
    }

    /// <summary>
    /// A commit sha, taken only from a backticked run or from a cell that is nothing but the sha.
    /// Loosening either would harvest hex-looking words out of prose.
    /// </summary>
    public static string? CommitSha(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var quoted = QuotedShaRegex().Match(value);
        if (quoted.Success)
        {
            return quoted.Groups[1].Value;
        }

        var bare = Plain(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return ShaRegex().IsMatch(bare) ? bare : null;
    }

    public static DateTimeOffset? Date(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = DateRegex().Match(value);
        return match.Success
               && DateTimeOffset.TryParseExact(
                   match.Value,
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The text with the control characters Postgres refuses in a <c>text</c> column removed — NUL above
    /// all, which ends a whole import with <c>22021: invalid byte sequence for encoding "UTF8": 0x00</c>.
    /// "Verbatim" cannot include a byte the store is unable to hold, so the removal is counted and
    /// reported rather than done quietly.
    /// </summary>
    public static (string Text, int Removed) Storable(string value)
    {
        var removed = value.Count(Unstorable);
        return removed == 0
            ? (value, 0)
            : (string.Concat(value.Where(c => !Unstorable(c))), removed);
    }

    private static bool Unstorable(char c)
        => char.IsControl(c) && c is not ('\n' or '\r' or '\t');

    public static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max].TrimEnd() + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^[0-9a-f]{7,40}$")]
    private static partial Regex ShaRegex();

    [GeneratedRegex(@"`([0-9a-f]{7,40})`")]
    private static partial Regex QuotedShaRegex();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex DateRegex();
}

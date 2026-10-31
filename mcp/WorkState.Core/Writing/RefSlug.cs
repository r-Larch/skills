using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkState.Core.Writing;

/// <summary>
/// The name an item under a project or the backlog takes when nobody chose one: a short kebab slug of its
/// title, so a human reads <c>voice-buffer-cap</c> where a number would say nothing. The create refusal
/// suggests it and a deferral lands on it, so the two never disagree.
/// </summary>
public static partial class RefSlug
{
    public const int MaxWords = 4;
    public const int MaxLength = 32;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "into", "is", "it", "its",
        "of", "on", "or", "so", "that", "the", "this", "to", "with",
    };

    private static readonly Dictionary<char, string> Spelled = new()
    {
        ['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe",
        ['ø'] = "o", ['å'] = "a", ['ł'] = "l", ['đ'] = "d", ['ð'] = "d", ['þ'] = "th", ['ı'] = "i",
    };

    /// <summary>The slug of <paramref name="title"/>, or <paramref name="fallback"/> when the title has no ASCII word.</summary>
    public static string From(string? title, string fallback = "item")
    {
        var words = Words(title ?? string.Empty);
        var kept = words.Where(w => !StopWords.Contains(w)).ToList();
        if (kept.Count == 0)
        {
            kept = words;
        }

        var slug = new StringBuilder();
        foreach (var word in kept.Take(MaxWords))
        {
            if (slug.Length == 0)
            {
                slug.Append(word.AsSpan(0, Math.Min(word.Length, MaxLength)));
                continue;
            }

            if (slug.Length + 1 + word.Length > MaxLength)
            {
                break;
            }

            slug.Append('-').Append(word);
        }

        var result = slug.ToString().Trim('-');
        if (result.Length == 0)
        {
            return fallback;
        }

        return IsNumbered(result) ? $"{fallback}-{result}" : result;
    }

    /// <summary>
    /// True for a ref the server numbers inside a run (<c>3</c>, <c>2.3</c>, <c>F-07</c>), and for the deferral
    /// suffixes grown from one (<c>3-d</c>, <c>2.3-d2</c>): none of them says what the item is.
    /// </summary>
    public static bool IsNumbered(string reference) => Numbered().IsMatch(reference.Trim());

    /// <summary>The <paramref name="n"/>th candidate for <paramref name="slug"/>: itself, then <c>-2</c>, <c>-3</c>.</summary>
    public static string Variant(string slug, int n) => n == 1 ? slug : $"{slug}-{n}";

    private static List<string> Words(string title)
    {
        var ascii = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (Spelled.TryGetValue(c, out var spelled))
            {
                ascii.Append(spelled);
                continue;
            }

            foreach (var d in c.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                ascii.Append(char.IsAsciiLetterOrDigit(d) ? d : ' ');
            }
        }

        return [.. ascii.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    [GeneratedRegex(@"^(F-)?\d+(\.\d+)*(-d\d*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();
}

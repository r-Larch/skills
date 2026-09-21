using System.Text;
using System.Text.RegularExpressions;
using WorkState.Import.Markdown;

namespace WorkState.Import.Extraction;

/// <summary>
/// Turning a corpus label into an <c>item.ref</c>. The handles in use are <c>2.1b</c>, <c>3.R1</c>,
/// <c>B-07</c>, <c>F-07</c> and <c>2.3–2.5 gate r2</c>; the last is a compound and must not collapse
/// onto task 2.3, because <c>UNIQUE (ref_scope_id, ref)</c> would then merge two different things.
/// </summary>
public static partial class Refs
{
    /// <summary>
    /// A row label split into the ref it belongs to and what it says about it. A ledger writes
    /// <c>1.1 DocumentBlocks</c> where the brief and the plan write <c>1.1</c>, and unless the ref is the
    /// handle alone those are two rows for one task — which is exactly what
    /// <c>UNIQUE (ref_scope_id, ref)</c> is meant to prevent. A label naming more than one handle
    /// (<c>1.4 + 1.5 gate fixes</c>, <c>2.3–2.5 gate r2</c>) keeps the compound ref instead: collapsing
    /// it onto its first handle would merge two different things.
    /// </summary>
    public static (string Ref, string Title)? ParseLabel(string? cell)
    {
        var plain = MdText.Plain(cell);
        if (plain.Length == 0)
        {
            return null;
        }

        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var head = Head(words[0]);
        var rest = string.Join(' ', words.Skip(1)).Trim();

        if (!HandleRegex().IsMatch(head))
        {
            return Parse(cell) is { } slug ? (slug, plain) : null;
        }

        return SecondHandleRegex().IsMatch(rest)
            ? (head + "-" + Slug(rest, 24), rest.Length > 0 ? rest : head)
            : (head, rest.Length > 0 ? rest : head);
    }

    public static string? Parse(string? cell)
    {
        var plain = MdText.Plain(cell);
        if (plain.Length == 0)
        {
            return null;
        }

        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var head = Head(words[0]);

        if (!HandleRegex().IsMatch(head))
        {
            return Slug(plain, 48) is { Length: > 0 } slug ? slug : null;
        }

        var rest = string.Join(' ', words.Skip(1));
        var restSlug = Slug(rest, 24);

        return restSlug.Length == 0 ? head : head + "-" + restSlug;
    }

    /// <summary>A ref for a label that is prose, e.g. a phase heading or a follow-up line.</summary>
    public static string Slug(string? value, int max)
    {
        var plain = MdText.Normalize(value);
        var slug = new StringBuilder(plain.Length);

        foreach (var c in plain)
        {
            if (char.IsLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        return result.Length <= max ? result : result[..max].Trim('-');
    }

    /// <summary>The phase number a task ref belongs to: <c>4.2b</c> → <c>4</c>, <c>3.R1</c> → <c>3</c>.</summary>
    public static string? PhaseOf(string reference)
    {
        var match = PhasePrefixRegex().Match(reference);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// True for a cell that is only a row number. The corpus's <c>#</c> column is sometimes the task
    /// handle (<c>1.1</c>) and sometimes the row's position, and a position is not an identity: two
    /// tables would both claim ref <c>1</c>.
    /// </summary>
    public static bool IsOrdinal(string? cell) => OrdinalRegex().IsMatch(MdText.Plain(cell));

    private static string Head(string word)
        => word.Trim('.', ',', ':', ';', '(', ')', '[', ']', '*', '—', '–', '-');

    [GeneratedRegex(@"^[A-Za-z]{0,3}-?[0-9][0-9A-Za-z.\-]*$")]
    private static partial Regex HandleRegex();

    [GeneratedRegex(@"(^|[\s+&/])[0-9]+\.[0-9]")]
    private static partial Regex SecondHandleRegex();

    [GeneratedRegex(@"^[0-9]{1,3}$")]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"^([0-9]+)\.")]
    private static partial Regex PhasePrefixRegex();
}

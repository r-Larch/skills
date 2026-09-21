using System.Text;
using WorkState.Core.Domain;

namespace WorkState.Core.Rendering;

/// <summary>
/// The flag vocabulary of <c>docs/workstate/06-rendering.md</c> §2. Each one names a state that
/// needs a decision, and each has exactly one call that resolves it — the table in the document is
/// the contract, so the strings live here rather than being spelled at each call site.
/// </summary>
public static class Flag
{
    public const string NoVerify = "⚠ no verify";
    public const string ReviewRequired = "review-req";

    public static string BlockedBy(string reference) => $"← {reference}";

    public static string Blocker(string target) => $"BLOCKER → {target}";

    public static string Revisit(string reference) => $"revisit: {reference}";

    public static string ClaimExpired(TimeSpan since) => $"claim exp {Glyph.Age(since)}";

    public static string OpenChildren(long count) => $"{count} open";
}

/// <summary>
/// One row of a board. It carries what the row <em>means</em>; the renderer decides how wide each
/// column is. There is no id on it, because doc 06 §1 rule 2 — a uuid the agent cannot use is
/// twelve wasted tokens — is enforced by the model shape, not by remembering.
/// </summary>
public sealed record BoardItem(
    string Ref,
    string Kind,
    ItemStatus Status,
    string Title,
    TimeSpan? DoingFor = null,
    string? Evidence = null,
    IReadOnlyList<string>? Flags = null);

/// <summary>A node of <c>work_tree</c>: a board row, its depth, and the rollup of its subtree.</summary>
public sealed record TreeItem(BoardItem Item, int Depth, string? Rollup = null);

public static class BoardRenderer
{
    /// <summary>doc 06 §2. Absent means normal, so an empty column is left empty and the line is
    /// trimmed — a board where every row carries <c>ok</c> has spent its budget saying nothing.</summary>
    public static string Row(BoardItem item)
    {
        var builder = new StringBuilder(Columns.Width);

        builder.Append(Glyph.Pad(Glyph.Flat(item.Ref), Columns.Ref)).Append("  ");
        builder.Append(Glyph.Pad(Glyph.Kind(item.Kind), Columns.Kind)).Append("  ");
        builder.Append(Glyph.Pad(Glyph.Status(item.Status, item.DoingFor), Columns.Status)).Append("  ");
        builder.Append(Glyph.Pad(Glyph.Clip(item.Title, Columns.Title), Columns.Title)).Append("  ");
        builder.Append(Glyph.Pad(item.Evidence ?? Glyph.None, Columns.Evidence)).Append("  ");

        if (item.Flags is { Count: > 0 })
        {
            builder.Append(string.Join("  ", item.Flags));
        }

        return builder.ToString().TrimEnd();
    }

    public static string Board(IEnumerable<BoardItem> items)
        => string.Join("\n", items.Select(Row));

    /// <summary>
    /// doc 06 §3 — two spaces per level, inside the handle column so the ref is still the first
    /// token of the line. Rollups appear on containers only; <c>—</c> where one is meaningless.
    /// </summary>
    public static string Tree(IEnumerable<TreeItem> nodes)
        => string.Join("\n", nodes.Select(TreeRow));

    public static string TreeRow(TreeItem node)
    {
        var indent = new string(' ', Math.Min(Math.Max(node.Depth, 0), 8) * 2);
        var handle = indent + Glyph.Clip(node.Item.Ref, Columns.TreeRef - indent.Length);

        var builder = new StringBuilder(Columns.Width);
        builder.Append(Glyph.Pad(handle, Columns.TreeRef)).Append("  ");
        builder.Append(Glyph.Pad(Glyph.Kind(node.Item.Kind), Columns.Kind)).Append("  ");
        builder.Append(Glyph.Pad(Glyph.Status(node.Item.Status, node.Item.DoingFor), Columns.Status)).Append("  ");
        builder.Append(Glyph.Pad(Glyph.Clip(node.Item.Title, Columns.Title), Columns.Title)).Append("  ");
        builder.Append(Glyph.Pad(node.Item.Evidence ?? Glyph.None, Columns.Evidence)).Append("  ");

        if (node.Rollup is { Length: > 0 })
        {
            builder.Append(node.Rollup);
        }

        if (node.Item.Flags is { Count: > 0 })
        {
            if (node.Rollup is { Length: > 0 })
            {
                builder.Append("  ");
            }

            builder.Append(string.Join("  ", node.Item.Flags));
        }

        return builder.ToString().TrimEnd();
    }
}

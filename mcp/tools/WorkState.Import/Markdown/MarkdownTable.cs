using System.Text;

namespace WorkState.Import.Markdown;

public sealed record TableRow(IReadOnlyList<string> Cells, string Raw, bool ArityMatches);

/// <summary>What one block's body held: its tables, the rows reunited with a header, and the rest.</summary>
public sealed record TableScan(
    IReadOnlyList<MarkdownTable> Tables,
    int AttachedRows,
    IReadOnlyList<string> OrphanRows);

/// <summary>
/// A GFM pipe table, matched on its header names rather than on column position: the corpus has four
/// variants of the task log alone (RECON-CORPUS §3), and position is the one thing they do not share.
/// </summary>
public sealed class MarkdownTable
{
    private MarkdownTable(IReadOnlyList<string> header, IReadOnlyList<TableRow> rows, string raw)
    {
        Header = header;
        Normalized = header.Select(MdText.Normalize).ToArray();
        Literal = header.Select(h => MdText.Plain(h).Trim().ToLowerInvariant()).ToArray();
        Rows = rows;
        Raw = raw;
    }

    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<string> Normalized { get; }

    /// <summary>
    /// The header with its punctuation intact. <see cref="MdText.Normalize"/> drops everything that is
    /// not a letter or digit, which leaves the corpus's ordinal column — literally <c>#</c> — with an
    /// empty name and unreachable by <see cref="Column"/>.
    /// </summary>
    public IReadOnlyList<string> Literal { get; }

    public IReadOnlyList<TableRow> Rows { get; }

    public string Raw { get; }

    /// <summary>
    /// The shape's fingerprint, used to count an unrecognised table in the report. Built from
    /// <see cref="Literal"/>: a report that prints <c>| | task | delivered |</c> hides the fact that the
    /// first column is the corpus's ordinal <c>#</c>.
    /// </summary>
    public string Signature
        => "| " + string.Join(" | ", Literal.Select(h => h.Length > 0 ? h : "(blank)")) + " |";

    public static IReadOnlyList<MarkdownTable> FindAll(string body) => Scan(body).Tables;

    /// <summary>
    /// Tables, plus the rows that belong to one and were written apart from it. The corpus interleaves
    /// commentary between task-log rows, so a reader that stops at the first blank line loses every row
    /// after the first — those are recovered here by arity and counted, never silently.
    /// </summary>
    public static TableScan Scan(string body)
    {
        var lines = body.Split('\n');
        var tables = new List<MarkdownTable>();
        var rowsByTable = new List<List<TableRow>>();
        var consumed = new bool[lines.Length];

        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!IsRow(lines[i]) || !IsSeparator(lines[i + 1]))
            {
                continue;
            }

            var header = Split(lines[i]);
            var raw = new StringBuilder().Append(lines[i]).Append('\n').Append(lines[i + 1]).Append('\n');
            var rows = new List<TableRow>();
            consumed[i] = consumed[i + 1] = true;

            var j = i + 2;
            for (; j < lines.Length && IsRow(lines[j]); j++)
            {
                var cells = Split(lines[j]);
                rows.Add(new TableRow(cells, lines[j], cells.Count == header.Count));
                raw.Append(lines[j]).Append('\n');
                consumed[j] = true;
            }

            tables.Add(new MarkdownTable(header, rows, raw.ToString()));
            rowsByTable.Add(rows);
            i = j - 1;
        }

        var attached = 0;
        var orphans = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (consumed[i] || !IsRow(lines[i]) || IsSeparator(lines[i]))
            {
                continue;
            }

            var cells = Split(lines[i]);
            var owner = -1;

            for (var t = 0; t < tables.Count; t++)
            {
                if (tables[t].Header.Count == cells.Count)
                {
                    owner = t;
                }
            }

            if (owner < 0)
            {
                orphans.Add(lines[i].Trim());
                continue;
            }

            rowsByTable[owner].Add(new TableRow(cells, lines[i], true));
            attached++;
        }

        return new TableScan(tables, attached, orphans);
    }

    /// <summary>The index of the first column whose normalized name matches, or -1.</summary>
    public int Column(params string[] names)
    {
        foreach (var name in names)
        {
            var literal = Literal.ToList().IndexOf(name);
            if (literal >= 0)
            {
                return literal;
            }

            var exact = Normalized.ToList().IndexOf(name);
            if (exact >= 0)
            {
                return exact;
            }
        }

        foreach (var name in names.Where(n => n.Length > 0))
        {
            for (var i = 0; i < Normalized.Count; i++)
            {
                if (Normalized[i].Length > 0 && Normalized[i].StartsWith(name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    public bool Has(params string[] names) => Column(names) >= 0;

    public static string? Cell(TableRow row, int index)
        => index >= 0 && index < row.Cells.Count ? row.Cells[index] : null;

    private static bool IsRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Length > 1;
    }

    private static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|'))
        {
            return false;
        }

        var inner = trimmed.Trim('|');
        return inner.Length > 0 && inner.All(c => c is '-' or ':' or '|' or ' ' or '\t') && inner.Contains('-');
    }

    /// <summary>Splits on unescaped pipes. A cell keeps its markdown; the typed readers normalize later.</summary>
    private static List<string> Split(string line)
    {
        var trimmed = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }

            if (c == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        cells.Add(cell.ToString().Trim());

        if (cells.Count > 0 && cells[0].Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && cells[^1].Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }
}

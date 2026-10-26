using System.Text;
using WorkState.Core.Domain;

namespace WorkState.Core.Rendering;

/// <summary>
/// The column grid of <c>docs/workstate/06-rendering.md</c> §2. Alignment is not decoration: it is
/// what lets a model scan a 40-line board instead of reading it, so the widths are constants and
/// the tests assert the resulting character positions.
/// </summary>
public static class Columns
{
    public const int Ref = 6;
    public const int Kind = 6;
    public const int Status = 9;
    public const int Title = 34;
    public const int Evidence = 12;
    public const int Gutter = 2;

    /// <summary>doc 06 §1 — the line budget every renderer stays inside.</summary>
    public const int Width = 100;

    /// <summary>The indented handle column of <c>work_tree</c>, wide enough for six levels.</summary>
    public const int TreeRef = 26;

    public static int KindAt => Ref + Gutter;
    public static int StatusAt => KindAt + Kind + Gutter;
    public static int TitleAt => StatusAt + Status + Gutter;
    public static int EvidenceAt => TitleAt + Title + Gutter;
    public static int FlagsAt => EvidenceAt + Evidence + Gutter;
}

/// <summary>
/// Vocabulary shared by every rendering: the abbreviations, the age format and the one row cut.
/// <c>ref</c> and <c>status</c> are never shortened — the ref is the handle and the status is the
/// decision.
/// </summary>
public static class Glyph
{
    public const string None = "—";
    public const string Warn = "⚠";
    public const string Ellipsis = "…";

    /// <summary>
    /// The 11 seeded item kinds plus <c>assumption</c>, which is rendered on a board although it is
    /// its own table. Six characters, because column 2 is six wide.
    /// </summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project"] = "proj",
        ["feature"] = "feat",
        ["run"] = "run",
        ["phase"] = "phase",
        ["task"] = "task",
        ["subtask"] = "subtsk",
        ["finding"] = "find",
        ["followup"] = "foll",
        ["question"] = "quest",
        ["incident"] = "incid",
        ["spike"] = "spike",
        ["assumption"] = "assum",
        ["note"] = "note",
    };

    public static string Kind(string key)
        => Kinds.TryGetValue(key, out var label) ? label : key;

    public static string Status(ItemStatus status, TimeSpan? doingFor = null) => status switch
    {
        ItemStatus.Open => "open",
        ItemStatus.Ready => "ready",
        ItemStatus.InProgress => doingFor is { } age ? $"doing {Age(age)}" : "doing",
        ItemStatus.Blocked => "blocked",
        ItemStatus.Review => "review",
        ItemStatus.Done => "done",
        ItemStatus.Cancelled => "cancel",
        ItemStatus.Superseded => "super",
        _ => status.ToString().ToLowerInvariant(),
    };

    /// <summary>Three characters at most, so <c>doing 43m</c> fits the nine-wide status column.</summary>
    public static string Age(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m",
        { TotalHours: < 24 } => $"{(int)span.TotalHours}h",
        { TotalDays: < 100 } => $"{(int)span.TotalDays}d",
        _ => "99d",
    };

    /// <summary>A past moment as a phrase: <c>43m ago</c>, or <c>just now</c> — never <c>now ago</c>.</summary>
    public static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? "just now" : $"{Age(span)} ago";

    /// <summary>Evidence as doc 06 §2 writes it: checked over total, or nothing to say.</summary>
    public static string Evidence(int passed, int total, bool failing = false)
        => total == 0 ? None : $"{(failing ? "✗" : "✓")}{passed}/{total}";

    public static string Flat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Shortens <paramref name="text"/> to one row cell. Allowed only where doc 06 "What may be
    /// shortened" allows it — a list row carrying the ref whose detail view prints the text whole,
    /// or a write confirmation echoing the caller's own input — and every caller is on the
    /// allow-list in <c>WorkState.Tests/Read/NoClipSourceTests.cs</c>. Never for a command, a ref or
    /// anything inside a route.
    /// </summary>
    public static string Row(string? text, int width)
    {
        var flat = Flat(text);
        return flat.Length <= width ? flat : flat[..(width - 1)] + Ellipsis;
    }

    public static string Pad(string text, int width)
        => text.Length >= width ? text : text + new string(' ', width - text.Length);
}

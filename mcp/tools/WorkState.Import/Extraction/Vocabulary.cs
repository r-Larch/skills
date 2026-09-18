using System.Text.RegularExpressions;
using WorkState.Core.Domain;
using WorkState.Import.Markdown;

namespace WorkState.Import.Extraction;

public sealed record Verdict(string Token, ItemStatus Status, ItemResolution? Resolution);

/// <summary>
/// The corpus's own words mapped onto doc 02 §1. Every token here was counted across all 181 files in
/// RECON-CORPUS §4; a token that is not here is recorded as unmapped rather than guessed at.
/// </summary>
public static partial class Vocabulary
{
    private static readonly Dictionary<string, Verdict> Verdicts = new(StringComparer.Ordinal)
    {
        ["accepted"] = new("ACCEPTED", ItemStatus.Done, ItemResolution.Accepted),
        ["accept"] = new("ACCEPTED", ItemStatus.Done, ItemResolution.Accepted),
        ["passed"] = new("PASSED", ItemStatus.Done, ItemResolution.Accepted),

        // Counted in the corpus but not in RECON-CORPUS §4's token table: a gate that passes with findings
        // is closed, and the findings are separate items — the token itself is kept in the event payload.
        ["pass with minors"] = new("PASS-WITH-MINORS", ItemStatus.Done, ItemResolution.Accepted),
        ["delivers with gaps"] = new("DELIVERS-WITH-GAPS", ItemStatus.Done, ItemResolution.Accepted),
        ["not started"] = new("NOT-STARTED", ItemStatus.Open, null),
        ["done"] = new("DONE", ItemStatus.Done, ItemResolution.Accepted),
        ["closed"] = new("CLOSED", ItemStatus.Done, ItemResolution.Accepted),
        ["reworked"] = new("REWORKED", ItemStatus.Done, ItemResolution.Reworked),
        ["re planned"] = new("RE-PLANNED", ItemStatus.Done, ItemResolution.Replanned),
        ["replanned"] = new("RE-PLANNED", ItemStatus.Done, ItemResolution.Replanned),
        ["escalated"] = new("ESCALATED", ItemStatus.Done, ItemResolution.Escalated),
        ["skipped"] = new("SKIPPED", ItemStatus.Done, ItemResolution.Skipped),
        ["wontfix"] = new("WONTFIX", ItemStatus.Done, ItemResolution.Wontfix),
        ["obsolete"] = new("OBSOLETE", ItemStatus.Done, ItemResolution.Obsolete),
        ["duplicate"] = new("DUPLICATE", ItemStatus.Done, ItemResolution.Duplicate),
        ["superseded"] = new("SUPERSEDED", ItemStatus.Superseded, null),
        ["cancelled"] = new("CANCELLED", ItemStatus.Cancelled, null),
        ["blocked"] = new("BLOCKED", ItemStatus.Blocked, null),
        ["failed"] = new("FAILED", ItemStatus.Review, null),
        ["review"] = new("REVIEW", ItemStatus.Review, null),
        ["in progress"] = new("IN-PROGRESS", ItemStatus.InProgress, null),
        ["open"] = new("OPEN", ItemStatus.Open, null),
        ["ready"] = new("READY", ItemStatus.Ready, null),
    };

    /// <summary>
    /// The first verdict token the cell contains. The corpus writes <c>REWORKED → ACCEPTED</c> and
    /// <c>✅ PASSED</c>, so the reader scans rather than compares.
    /// </summary>
    public static Verdict? ReadVerdict(string? cell)
    {
        var normalized = MdText.Normalize(cell);
        if (normalized.Length == 0)
        {
            return null;
        }

        var padded = " " + normalized + " ";
        Verdict? best = null;
        var bestAt = int.MaxValue;

        foreach (var (token, verdict) in Verdicts)
        {
            var at = padded.IndexOf(" " + token + " ", StringComparison.Ordinal);
            if (at < 0 || at >= bestAt)
            {
                continue;
            }

            best = verdict;
            bestAt = at;
        }

        return best;
    }

    /// <summary>
    /// A verdict in a heading or a log line, where the text around it is prose. Only a shouted token
    /// counts: the corpus writes every outcome in capitals (RECON-CORPUS §4), so
    /// <c>### 1.1 — review skipped, and the boundary …</c> is a sentence about a task, not a verdict on
    /// it, and reading it as one silently reopens an item a table had already closed.
    /// </summary>
    public static Verdict? ReadShoutedVerdict(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var shouted = string.Join(' ', ShoutRegex().Matches(text).Select(m => m.Value));
        return shouted.Length == 0 ? null : ReadVerdict(shouted);
    }

    [GeneratedRegex(@"\b[A-Z][A-Z-]{2,}\b")]
    private static partial Regex ShoutRegex();

    /// <summary>
    /// The evidence level, read from the leading clause before the whole cell. The corpus writes the
    /// level first and its provenance after — <c>reasoned; one TOAST read per call</c> — and a reader
    /// scanning the whole cell at once would grade that as <c>code_read</c> on the word "read".
    /// </summary>
    public static EvidenceLevel? ReadEvidence(string? cell)
        => Level(FirstClause(cell)) ?? Level(cell);

    private static string? FirstClause(string? cell)
    {
        if (cell is null)
        {
            return null;
        }

        var cut = cell.IndexOfAny([';', ',', ':', '(', '—', '–']);
        return cut > 0 ? cell[..cut] : cell;
    }

    private static EvidenceLevel? Level(string? cell)
    {
        var text = MdText.Normalize(cell);
        if (text.Length == 0)
        {
            return null;
        }

        if (Contains(text, "measur", "counted", "benchmark", "profiled"))
        {
            return EvidenceLevel.Measured;
        }

        if (Contains(text, "probe", "tested", "ran ", "verified", "reproduced", "experiment"))
        {
            return EvidenceLevel.Probed;
        }

        if (Contains(text, "code", "read", "grep", "recon", "source", "repo"))
        {
            return EvidenceLevel.CodeRead;
        }

        if (Contains(text, "reason", "inferred", "analysis", "strong", "derived", "doc"))
        {
            return EvidenceLevel.Reasoned;
        }

        if (Contains(text, "none", "asserted", "owner", "stated", "claim", "assumed", "no evidence"))
        {
            return EvidenceLevel.Asserted;
        }

        return null;
    }

    public static ReversalCost? ReadReversal(string? cell)
    {
        var text = MdText.Normalize(cell);
        if (text.Length == 0)
        {
            return null;
        }

        if (Contains(text, "irreversible", "permanent", "cannot be undone", "unrecoverable"))
        {
            return ReversalCost.Irreversible;
        }

        if (Contains(text, "trivial", "none", "zero", "nil", "free"))
        {
            return ReversalCost.Trivial;
        }

        if (Contains(text, "very high", "high"))
        {
            return ReversalCost.High;
        }

        if (Contains(text, "medium", "moderate"))
        {
            return ReversalCost.Medium;
        }

        return Contains(text, "low", "cheap", "small") ? ReversalCost.Low : null;
    }

    /// <summary>item.risk_tags, doc 02 §3 — only the ten the column documents, never an invented tag.</summary>
    private static readonly string[] RiskVocabulary =
    [
        "auth", "tenancy", "schema", "migration", "contract", "identity",
        "money", "delete", "equivalence", "production",
    ];

    public static IReadOnlyList<string> ReadRiskTags(string? text)
    {
        var normalized = MdText.Normalize(text);
        return normalized.Length == 0
            ? []
            : RiskVocabulary.Where(tag => normalized.Contains(tag, StringComparison.Ordinal)).ToArray();
    }

    private static bool Contains(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
}

namespace WorkState.Core.Rendering;

/// <summary>
/// Cost of ignoring, highest first (doc 06 §5). The order is the ranking: five lines is the whole
/// budget, so what gets cut is decided here rather than by whichever query ran last.
/// </summary>
public enum AttentionRank
{
    ExpiredClaim = 0,
    BlockerFinding = 1,

    /// <summary>
    /// doc 07 §2 and FINDINGS-live F-L1b: a phase whose tasks are all done is <em>ready to be
    /// gated</em>, and nothing transitions it. It outranks a single task's missing verification
    /// because what goes unexamined is the composition across every task in the phase.
    /// </summary>
    PhaseGateNotRun = 2,

    DoneUnverified = 3,
    AssumptionPastRevisit = 4,
    UnconsumedNote = 5,
    StaleInProgress = 6,
}

/// <summary>
/// One attention line. <paramref name="Call"/> is not optional and not prose: a line without a call
/// is the 1,241 hand-written ⚠ markers of the old corpus reinvented, which is what this replaces.
/// <para>
/// <paramref name="Ref"/> is carried beside <paramref name="What"/>, never instead of it: the
/// rendered text is one sentence by design (doc 06 §5), but doc 09 §5's queue gives the ref its own
/// column, and re-extracting it from prose in the client is how the two surfaces drift apart.
/// </para>
/// <para>
/// <paramref name="QualifiedRef"/> is what the queue links on. A bare ref is unique only inside its
/// ref scope, and the project-scoped attention block spans every run under a project — the live corpus has one
/// <c>phase-4</c> in each of four runs. Every source names one item, the unconsumed-note line
/// included: it is one line per addressee (ws/13), so its call can never name an ambiguous scope.
/// </para>
/// </summary>
public sealed record AttentionLine(
    AttentionRank Rank,
    string What,
    string Call,
    string? Ref = null,
    string? QualifiedRef = null);

/// <summary>
/// The attention lines of one scope, carrying the ref their uncapped block is read back under, so a
/// capped block names <c>work://attention/&lt;scope&gt;</c> without every caller passing it along.
/// </summary>
public sealed class ScopedAttention(string scope) : List<AttentionLine>
{
    public string Scope { get; } = scope;
}

public static class Attention
{
    public const int MaxLines = 5;

    /// <summary>
    /// Renders the block, or the empty string. Never an empty header: doc 06 §1 rule 5, and it is
    /// the difference between a nag channel and thirty wasted tokens on every call. The count cap
    /// names the read that lists everything it cut (doc 06 "What may be shortened").
    /// </summary>
    public static string Block(IEnumerable<AttentionLine> lines)
    {
        var all = lines.OrderBy(l => l.Rank).ToList();
        var ranked = all.Take(MaxLines).ToList();

        if (ranked.Count == 0)
        {
            return string.Empty;
        }

        var body = ranked.Select(Line).ToList();
        if (all.Count == ranked.Count)
        {
            return $"--- attention ({ranked.Count}) ---\n{string.Join("\n", body)}";
        }

        var uri = WorkUris.AttentionFor((lines as ScopedAttention)?.Scope);
        body.Add($"+{all.Count - ranked.Count} more — read {uri} with ReadMcpResourceTool");
        return $"--- attention ({ranked.Count} of {all.Count}) ---\n{string.Join("\n", body)}";
    }

    /// <summary>Every line, ranked and uncapped — what <c>work://attention/&lt;scope&gt;</c> serves.</summary>
    public static string Full(string scope, IEnumerable<AttentionLine> lines)
    {
        var ranked = lines.OrderBy(l => l.Rank).ToList();
        return ranked.Count == 0
            ? $"ATTENTION  nothing needs attention under {scope}."
            : $"--- attention ({ranked.Count}) under {scope} ---\n{string.Join("\n", ranked.Select(Line))}";
    }

    /// <summary>Appends the block to a result, or leaves the result alone when there is nothing to say.</summary>
    public static string Append(string body, IEnumerable<AttentionLine> lines)
    {
        var block = Block(lines);
        return block.Length == 0 ? body : $"{body}\n\n{block}";
    }

    private static string Line(AttentionLine line) => $"{Glyph.Warn} {Glyph.Flat(line.What)} — {Glyph.Flat(line.Call)}";
}

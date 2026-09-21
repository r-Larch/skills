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
/// </summary>
public sealed record AttentionLine(AttentionRank Rank, string What, string Call);

public static class Attention
{
    public const int MaxLines = 5;

    /// <summary>
    /// Renders the block, or the empty string. Never an empty header: doc 06 §1 rule 5, and it is
    /// the difference between a nag channel and thirty wasted tokens on every call.
    /// </summary>
    public static string Block(IEnumerable<AttentionLine> lines)
    {
        var ranked = lines
            .OrderBy(l => l.Rank)
            .Take(MaxLines)
            .ToList();

        if (ranked.Count == 0)
        {
            return string.Empty;
        }

        var body = ranked.Select(l => $"{Glyph.Warn} {Glyph.Flat(l.What)} — {Glyph.Flat(l.Call)}");
        return $"--- attention ({ranked.Count}) ---\n{string.Join("\n", body)}";
    }

    /// <summary>Appends the block to a result, or leaves the result alone when there is nothing to say.</summary>
    public static string Append(string body, IEnumerable<AttentionLine> lines)
    {
        var block = Block(lines);
        return block.Length == 0 ? body : $"{body}\n\n{block}";
    }
}

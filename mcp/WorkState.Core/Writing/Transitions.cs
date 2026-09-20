using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// What <c>work_status(to:)</c> accepts. <see cref="Deferred"/> is here and <b>not</b> in
/// <see cref="ItemStatus"/> on purpose: doc 07 §4 — deferral is a move that creates a real item
/// elsewhere, and a <c>deferred</c> state is where work goes to disappear.
/// </summary>
public enum Move
{
    Ready,
    InProgress,
    Blocked,
    Review,
    Done,
    Cancelled,
    Deferred,
    Superseded,
}

/// <summary>
/// doc 07 §2, as a table rather than a chain of <c>if</c>s. Pure: it answers which moves are legal
/// from a status and what each one demands, and nothing here reads the store.
/// </summary>
public static class Transitions
{
    private static readonly ItemStatus[] Terminal =
        [ItemStatus.Done, ItemStatus.Cancelled, ItemStatus.Superseded];

    private static readonly Dictionary<Move, ItemStatus[]> From = new()
    {
        [Move.Ready] = [ItemStatus.Open, ItemStatus.Blocked],
        [Move.InProgress] = [ItemStatus.Open, ItemStatus.Ready, ItemStatus.Blocked, ItemStatus.InProgress, ItemStatus.Review],
        [Move.Blocked] = [ItemStatus.Open, ItemStatus.Ready, ItemStatus.InProgress, ItemStatus.Review],
        [Move.Review] = [ItemStatus.InProgress],
        [Move.Done] = [ItemStatus.InProgress, ItemStatus.Review],
    };

    /// <summary>
    /// A finding is a record, not a piece of work: doc 06 §6's refusal and every attention line
    /// print <c>work_status(ref:"F-07", to:"done")</c> against a finding that is still
    /// <c>open</c>, and doc 04 §3 makes a printed route that refuses a defect. So <c>done</c> is
    /// reachable from any non-terminal status for this kind alone — the five conditions still
    /// apply, so nothing is weakened by skipping a claim nobody takes on a finding.
    /// </summary>
    private const string ClosesDirectly = "finding";

    public static bool IsTerminal(ItemStatus status) => Terminal.Contains(status);

    public static bool TryParse(string? to, out Move move)
    {
        move = default;
        if (string.IsNullOrWhiteSpace(to))
        {
            return false;
        }

        return Enum.TryParse(to.Replace("_", string.Empty).Trim(), ignoreCase: true, out move)
               && Enum.IsDefined(move);
    }

    public static string Name(Move move) => move switch
    {
        Move.InProgress => "in_progress",
        _ => move.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The status as <c>work_status(to:)</c> spells it — not <see cref="Glyph.Status"/>, which
    /// abbreviates for a nine-wide board column. A refusal quotes what the caller can type.
    /// </summary>
    public static string Name(ItemStatus status) => status switch
    {
        ItemStatus.InProgress => "in_progress",
        _ => status.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The moves that are legal right now, in the order doc 07 §2 draws them. The refusal for an
    /// illegal move lists these, because a prohibition without its route is a trap (doc 04 §3).
    /// </summary>
    public static IReadOnlyList<Move> LegalFrom(ItemStatus status, string? kind = null)
    {
        if (IsTerminal(status))
        {
            return [];
        }

        var moves = new List<Move>();
        foreach (var (move, sources) in From)
        {
            if (sources.Contains(status))
            {
                moves.Add(move);
            }
        }

        if (string.Equals(kind, ClosesDirectly, StringComparison.OrdinalIgnoreCase) && !moves.Contains(Move.Done))
        {
            moves.Add(Move.Done);
        }

        moves.Add(Move.Cancelled);
        moves.Add(Move.Deferred);
        return [.. moves.OrderBy(m => (int)m)];
    }

    public static bool Legal(ItemStatus from, Move to, string? kind = null)
        => LegalFrom(from, kind).Contains(to);

    /// <summary>
    /// doc 07 §2's two rows that are refusals rather than transitions, plus the generic illegal
    /// move. Each names the call that actually achieves what the caller was reaching for.
    /// </summary>
    public static string Refuse(string reference, ItemStatus from, Move to, string? kind = null)
    {
        var headline = $"work_status({reference} → {Name(to)}). {reference} is {Name(from)}.";

        if (to == Move.Superseded)
        {
            return Refusal.Blocked(
                $"work_status({reference} → superseded) is never a direct move.",
                "superseded is set by the link that replaces the item, in the same transaction",
                $"work_link(from:\"<the replacement>\", to:\"{reference}\", type:\"supersedes\")");
        }

        if (IsTerminal(from))
        {
            return Refusal.Blocked(
                headline,
                $"{Name(from)} is terminal — reopening would erase the record of the close",
                $"work_create(items:[{{kind:\"followup\", parent:\"{reference}\", title:\"…\", "
                + "verify_cmd:\"…\", criteria:[…]}]) to carry the remaining work forward",
                $"work_link(from:\"<the new item>\", to:\"{reference}\", type:\"supersedes\") "
                + "if it replaces this outright");
        }

        var legal = LegalFrom(from, kind).Select(Name).ToList();
        return Refusal.Blocked(
            headline,
            $"{Name(to)} is not reachable from {Name(from)}",
            $"work_status(ref:\"{reference}\", to:\"{legal[0]}\") — legal from here: {string.Join(", ", legal)}");
    }
}

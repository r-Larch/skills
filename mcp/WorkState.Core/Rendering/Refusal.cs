namespace WorkState.Core.Rendering;

/// <summary>
/// One unmet condition and the calls that satisfy it. <paramref name="Routes"/> is never empty:
/// doc 04 §3 makes an error that states a problem without naming the fix a defect, and doc 06 §6
/// turns that into a shape — every <c>✗</c> is followed by a <c>→</c>.
/// </summary>
public sealed record Unmet(string Problem, IReadOnlyList<string> Routes)
{
    public Unmet(string problem, string route) : this(problem, [route]) { }
}

/// <summary>
/// doc 06 §6. The highest-value text the server emits: it is read at the exact moment it is
/// relevant, so it teaches the closure contract permanently in one round trip.
/// Pure, like the rest of <c>Rendering/</c> — no database, no web host.
/// </summary>
public static class Refusal
{
    private const int RouteIndent = 6;
    private const int OverrideIndent = 2;

    /// <summary>
    /// A refused transition, with <b>every</b> unmet condition and the override named. Listing one
    /// failure at a time costs three round trips to find three problems, which reads as an
    /// adversarial system and teaches an agent to reach for <c>force</c> — the one outcome that
    /// makes the whole design pointless.
    /// </summary>
    public static string Transition(string reference, string to, IReadOnlyList<Unmet> unmet)
    {
        var lines = new List<string>
        {
            $"REFUSED  work_status({reference} → {to}). {Plural(unmet.Count, "condition")} unmet.",
            string.Empty,
        };

        lines.AddRange(unmet.SelectMany(Condition));

        lines.Add(string.Empty);
        lines.Add(
            $"{Pad(OverrideIndent)}Override: work_status(ref:\"{reference}\", to:\"{to}\", force:true, "
            + "reason:\"…\") — recorded as a");
        lines.Add($"{Pad(OverrideIndent)}forced_transition, which work_audit lists permanently.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// A refusal with no override: an illegal move, a missing required argument, a constraint
    /// Postgres owns. <c>force</c> is deliberately absent — doc 07 §1, a rule whose violation
    /// should be impossible is a constraint, and a constraint can only say no.
    /// </summary>
    public static string Blocked(string headline, IReadOnlyList<Unmet> unmet)
    {
        var lines = new List<string> { $"REFUSED  {Glyph.Flat(headline)}", string.Empty };
        lines.AddRange(unmet.SelectMany(Condition));
        return string.Join("\n", lines);
    }

    public static string Blocked(string headline, string problem, params string[] routes)
        => Blocked(headline, [new Unmet(problem, routes)]);

    private static IEnumerable<string> Condition(Unmet unmet)
    {
        yield return $"{Pad(OverrideIndent)}✗ {Glyph.Flat(unmet.Problem)}";

        for (var i = 0; i < unmet.Routes.Count; i++)
        {
            // "or" trails the line it belongs to rather than opening the next, so a reader scanning
            // the → column sees each alternative start in the same place (doc 06 §6's sample).
            var tail = i < unmet.Routes.Count - 1 ? "   or" : string.Empty;
            yield return $"{Pad(RouteIndent)}→ {Glyph.Flat(unmet.Routes[i])}{tail}";
        }
    }

    private static string Plural(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Pad(int width) => new(' ', width);
}

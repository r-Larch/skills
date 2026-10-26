using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

public sealed record CriterionFact(int Seq, string Text, bool Checked);

public sealed record VerificationFact(bool Passed, string? Command, VerifyRunner RunBy);

public sealed record BlockerFact(string Ref, string Title);

/// <summary>
/// Everything doc 07 §3 reads, gathered in one round trip. It is one record rather than five queries
/// interleaved with five checks because the refusal has to list <b>every</b> unmet condition at once
/// (doc 06 §6): a guard that returns at the first failure costs three round trips to find three
/// problems and teaches an agent to reach for <c>force</c>.
/// </summary>
public sealed record DoneFacts(
    string Ref,
    string Kind,
    string? VerifyCmd,
    bool RequiresVerify,
    bool RequiresCommit,
    bool HasCommit,
    IReadOnlyList<CriterionFact> Criteria,
    IReadOnlyList<VerificationFact> Verifications,
    IReadOnlyList<string> OpenChildren,
    IReadOnlyList<BlockerFact> Blockers);

/// <summary>
/// The five conditions of doc 07 §3, as a pure function of the facts. No database here, so the
/// wording and the completeness of a refusal are unit-testable without a container.
/// </summary>
public static class DoneGuard
{
    /// <summary>How many of a repeating condition are spelled out before the rest are counted.</summary>
    private const int Listed = 3;

    public static IReadOnlyList<Unmet> Evaluate(DoneFacts facts)
    {
        var unmet = new List<Unmet>();

        Criteria(facts, unmet);
        Verification(facts, unmet);
        Children(facts, unmet);
        Blockers(facts, unmet);
        Commit(facts, unmet);

        return unmet;
    }

    /// <summary>Condition 1 — the 424 unchecked boxes in runs whose ledgers declared them finished.</summary>
    private static void Criteria(DoneFacts facts, List<Unmet> unmet)
    {
        var open = facts.Criteria.Where(c => !c.Checked).ToList();

        foreach (var criterion in open.Take(Listed))
        {
            unmet.Add(new Unmet(
                $"criterion c{criterion.Seq} \"{Glyph.Flat(criterion.Text)}\" unchecked",
                $"work_update(ref:\"{facts.Ref}\", criteria:[{{id:\"c{criterion.Seq}\", "
                + "checked:true, evidence:\"…\"}])"));
        }

        if (open.Count > Listed)
        {
            unmet.Add(new Unmet(
                $"{open.Count - Listed} further criteria are unchecked",
                $"work_get(refs:[\"{facts.Ref}\"]) lists every one with its number"));
        }
    }

    /// <summary>
    /// Condition 2. <c>run_by = 'orchestrator'</c> is the essential half; command matching applies
    /// only when both commands are present, because <c>verification.command</c> is nullable and the
    /// corpus records the result far more often than the command (doc 07 §3, 2026-09-20).
    /// </summary>
    private static void Verification(DoneFacts facts, List<Unmet> unmet)
    {
        if (!facts.RequiresVerify)
        {
            return;
        }

        var route =
            $"work_verify(ref:\"{facts.Ref}\", command:\"{(string.IsNullOrWhiteSpace(facts.VerifyCmd) ? "…" : facts.VerifyCmd)}\", "
            + "result_line:\"…\", run_by:\"orchestrator\")";

        var passed = facts.Verifications.Where(v => v.Passed).ToList();
        var byOrchestrator = passed.Where(v => v.RunBy == VerifyRunner.Orchestrator).ToList();

        if (byOrchestrator.Count == 0)
        {
            var have = passed.Count == 0
                ? "none recorded"
                : $"{passed.Count} recorded, none of them run by the orchestrator";

            unmet.Add(new Unmet($"no verification with run_by=orchestrator ({have})", route));
            return;
        }

        if (facts.VerifyCmd is not { Length: > 0 } expected)
        {
            return;
        }

        var mismatched = byOrchestrator
            .Where(v => v.Command is { Length: > 0 })
            .ToList();

        if (mismatched.Count == byOrchestrator.Count
            && mismatched.TrueForAll(v => !Same(v.Command!, expected)))
        {
            unmet.Add(new Unmet(
                $"the orchestrator verification ran \"{Glyph.Flat(mismatched[0].Command)}\", "
                + $"not this item's verify_cmd \"{Glyph.Flat(expected)}\"",
                route));
        }
    }

    /// <summary>Condition 3 — a phase closed over unfinished tasks.</summary>
    private static void Children(DoneFacts facts, List<Unmet> unmet)
    {
        if (facts.OpenChildren.Count == 0)
        {
            return;
        }

        var shown = string.Join(", ", facts.OpenChildren.Take(Listed));
        var hidden = facts.OpenChildren.Count - Listed;

        unmet.Add(new Unmet(
            $"{facts.OpenChildren.Count} child item(s) still open: {shown}"
            + (hidden > 0 ? $" +{hidden} more" : string.Empty),
            [
                $"work_status(ref:\"{facts.OpenChildren[0]}\", to:\"done\")",
                $"work_tree(scope:\"{facts.Ref}\", status:\"open,ready,in_progress,blocked,review\")",
            ]));
    }

    /// <summary>Condition 4 — gate blockers dissolved into prose.</summary>
    private static void Blockers(DoneFacts facts, List<Unmet> unmet)
    {
        foreach (var blocker in facts.Blockers.Take(Listed))
        {
            unmet.Add(new Unmet(
                $"finding {blocker.Ref} (BLOCKER) targets {facts.Ref} and is open",
                [
                    $"work_status(ref:\"{blocker.Ref}\", to:\"done\")",
                    $"work_status(ref:\"{blocker.Ref}\", to:\"deferred\", target:\"backlog\", reason:\"…\")",
                ]));
        }

        if (facts.Blockers.Count > Listed)
        {
            unmet.Add(new Unmet(
                $"{facts.Blockers.Count - Listed} further blocker findings target {facts.Ref}",
                $"work_threads(scope:\"{facts.Ref}\", include:\"findings\")"));
        }
    }

    /// <summary>Condition 5 — "every accepted task ends in a commit", for the kinds that say so.</summary>
    private static void Commit(DoneFacts facts, List<Unmet> unmet)
    {
        if (!facts.RequiresCommit || facts.HasCommit)
        {
            return;
        }

        unmet.Add(new Unmet(
            $"no commit artifact recorded, and kind \"{facts.Kind}\" requires one",
            $"work_status(ref:\"{facts.Ref}\", to:\"done\", commit:\"<sha>\")"));
    }

    private static bool Same(string left, string right)
        => string.Equals(Glyph.Flat(left), Glyph.Flat(right), StringComparison.OrdinalIgnoreCase);
}

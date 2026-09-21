using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using Xunit;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 07 §3 as a pure function. Every refusal here has its permissive twin in the same file: a
/// guard that refuses everything passes every refusal test that can be written, and that is the
/// over-correction this unit is most at risk of.
/// </summary>
public sealed class DoneGuardTests
{
    private static DoneFacts Facts(
        IReadOnlyList<CriterionFact>? criteria = null,
        IReadOnlyList<VerificationFact>? verifications = null,
        IReadOnlyList<string>? children = null,
        IReadOnlyList<BlockerFact>? blockers = null,
        bool requiresVerify = true,
        bool requiresCommit = false,
        bool hasCommit = false,
        string? verifyCmd = "dotnet test --filter Guard")
        => new(
            "2.4",
            "task",
            verifyCmd,
            requiresVerify,
            requiresCommit,
            hasCommit,
            criteria ?? [],
            verifications ?? [new VerificationFact(true, "dotnet test --filter Guard", VerifyRunner.Orchestrator)],
            children ?? [],
            blockers ?? []);

    [Fact]
    public void An_item_that_meets_every_condition_is_allowed_through()
    {
        var unmet = DoneGuard.Evaluate(Facts(criteria: [new CriterionFact(1, "it works", true)]));

        Assert.Empty(unmet);
    }

    [Fact]
    public void An_unchecked_criterion_refuses_and_a_checked_one_does_not()
    {
        var refused = DoneGuard.Evaluate(Facts(criteria: [new CriterionFact(2, "DeletesSkipped surfaced", false)]));
        var allowed = DoneGuard.Evaluate(Facts(criteria: [new CriterionFact(2, "DeletesSkipped surfaced", true)]));

        var single = Assert.Single(refused);
        Assert.Contains("criterion c2", single.Problem, StringComparison.Ordinal);
        Assert.Contains("work_update(ref:\"2.4\", criteria:[{id:\"c2\"", single.Routes[0], StringComparison.Ordinal);
        Assert.Empty(allowed);
    }

    [Fact]
    public void A_worker_only_verification_refuses_and_an_orchestrator_one_does_not()
    {
        var refused = DoneGuard.Evaluate(Facts(
            verifications: [new VerificationFact(true, "dotnet test --filter Guard", VerifyRunner.Worker)]));

        var allowed = DoneGuard.Evaluate(Facts(
            verifications:
            [
                new VerificationFact(true, "dotnet test --filter Guard", VerifyRunner.Worker),
                new VerificationFact(true, "dotnet test --filter Guard", VerifyRunner.Orchestrator),
            ]));

        var single = Assert.Single(refused);
        Assert.Contains("run_by=orchestrator", single.Problem, StringComparison.Ordinal);
        Assert.Contains("run_by:\"orchestrator\"", single.Routes[0], StringComparison.Ordinal);
        Assert.Empty(allowed);
    }

    [Fact]
    public void A_failing_orchestrator_verification_is_not_evidence()
    {
        var unmet = DoneGuard.Evaluate(Facts(
            verifications: [new VerificationFact(false, "dotnet test --filter Guard", VerifyRunner.Orchestrator)]));

        Assert.Single(unmet);
    }

    /// <summary>
    /// doc 07 §3, loosened 2026-09-20: <c>verification.command</c> is nullable because the corpus
    /// records the result far more often than the command, and a guard that cannot be satisfied
    /// honestly teaches an agent to reach for <c>force</c>.
    /// </summary>
    [Fact]
    public void A_null_command_does_not_fail_the_guard_but_a_wrong_one_does()
    {
        var allowed = DoneGuard.Evaluate(Facts(
            verifications: [new VerificationFact(true, null, VerifyRunner.Orchestrator)]));

        var refused = DoneGuard.Evaluate(Facts(
            verifications: [new VerificationFact(true, "dotnet build", VerifyRunner.Orchestrator)]));

        Assert.Empty(allowed);
        Assert.Contains("not this item's verify_cmd", Assert.Single(refused).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_that_does_not_require_verification_closes_without_one()
    {
        var unmet = DoneGuard.Evaluate(Facts(verifications: [], requiresVerify: false));

        Assert.Empty(unmet);
    }

    [Fact]
    public void An_open_child_refuses_and_a_closed_one_does_not()
    {
        var refused = DoneGuard.Evaluate(Facts(children: ["2.4.1"]));
        var allowed = DoneGuard.Evaluate(Facts(children: []));

        var single = Assert.Single(refused);
        Assert.Contains("2.4.1", single.Problem, StringComparison.Ordinal);
        Assert.Contains("work_status(ref:\"2.4.1\", to:\"done\")", single.Routes[0], StringComparison.Ordinal);
        Assert.Empty(allowed);
    }

    [Fact]
    public void An_open_blocker_finding_refuses_and_names_both_ways_to_clear_it()
    {
        var refused = DoneGuard.Evaluate(Facts(blockers: [new BlockerFact("F-07", "ContentHash written early")]));

        var single = Assert.Single(refused);
        Assert.Contains("F-07 (BLOCKER)", single.Problem, StringComparison.Ordinal);
        Assert.Equal(2, single.Routes.Count);
        Assert.Contains("to:\"done\"", single.Routes[0], StringComparison.Ordinal);
        Assert.Contains("to:\"deferred\"", single.Routes[1], StringComparison.Ordinal);
        Assert.Empty(DoneGuard.Evaluate(Facts(blockers: [])));
    }

    [Fact]
    public void A_kind_that_requires_a_commit_refuses_without_one_and_passes_with_one()
    {
        var refused = DoneGuard.Evaluate(Facts(requiresCommit: true, hasCommit: false));
        var allowed = DoneGuard.Evaluate(Facts(requiresCommit: true, hasCommit: true));

        Assert.Contains("commit artifact", Assert.Single(refused).Problem, StringComparison.Ordinal);
        Assert.Contains("commit:\"<sha>\"", Assert.Single(refused).Routes[0], StringComparison.Ordinal);
        Assert.Empty(allowed);
    }

    /// <summary>
    /// doc 06 §6 — three round trips to discover three problems reads as an adversarial system and
    /// teaches an agent to reach for the override instead of the remedy.
    /// </summary>
    [Fact]
    public void An_item_failing_three_conditions_is_told_all_three_at_once()
    {
        var unmet = DoneGuard.Evaluate(Facts(
            criteria: [new CriterionFact(2, "DeletesSkipped surfaced on DatasetSourceRun", false)],
            verifications: [new VerificationFact(true, "dotnet test --filter Guard", VerifyRunner.Worker)],
            blockers: [new BlockerFact("F-07", "ContentHash written before the work")]));

        var rendered = Refusal.Transition("2.4", "done", unmet);

        Assert.Equal(3, unmet.Count);
        Assert.Equal(3, RefusalShape.Conditions(rendered));
        Assert.Contains("3 conditions unmet", rendered, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(rendered);
        RefusalShape.NamesTheOverride(rendered);
    }

    [Fact]
    public void Every_rendered_done_refusal_carries_a_callable_route_on_every_condition()
    {
        DoneFacts[] cases =
        [
            Facts(criteria: [new CriterionFact(1, "a", false), new CriterionFact(2, "b", false)]),
            Facts(verifications: []),
            Facts(verifications: [new VerificationFact(true, "dotnet build", VerifyRunner.Orchestrator)]),
            Facts(children: ["2.4.1", "2.4.2", "2.4.3", "2.4.4"]),
            Facts(blockers: [new BlockerFact("F-01", "x"), new BlockerFact("F-02", "y")]),
            Facts(requiresCommit: true),
            Facts(verifyCmd: null, verifications: []),
        ];

        foreach (var facts in cases)
        {
            var unmet = DoneGuard.Evaluate(facts);
            Assert.NotEmpty(unmet);
            RefusalShape.SelfCorrecting(Refusal.Transition(facts.Ref, "done", unmet));
        }
    }

    [Fact]
    public void More_unchecked_criteria_than_are_listed_are_counted_rather_than_hidden()
    {
        var unmet = DoneGuard.Evaluate(Facts(criteria:
        [
            new CriterionFact(1, "a", false),
            new CriterionFact(2, "b", false),
            new CriterionFact(3, "c", false),
            new CriterionFact(4, "d", false),
            new CriterionFact(5, "e", false),
        ]));

        Assert.Equal(4, unmet.Count);
        Assert.Contains("2 further criteria", unmet[^1].Problem, StringComparison.Ordinal);
    }
}

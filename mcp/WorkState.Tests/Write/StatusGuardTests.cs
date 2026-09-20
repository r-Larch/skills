using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 07 §2–§4 against a real database. Every refusal here has its permissive twin in the same
/// test: a guard that refuses honest work as well as dishonest work passes every refusal test that
/// can be written, and it is the failure mode that makes the whole design pointless.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class StatusGuardTests(PostgresFixture fixture)
{
    private static readonly StatusArgs None = new();

    [Fact]
    public async Task Done_is_refused_while_a_criterion_is_unchecked_and_allowed_once_it_is_checked()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        await WriteSeed.CriterionAsync(world, world.TaskId, 2, "DeletesSkipped surfaced", ticked: false);

        var refused = await Status(world, "1.1", "done");

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("criterion c2", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));

        await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "1.1",
            new UpdateArgs(Criteria: [new CriterionArg("c2", Checked: true, Evidence: "the assertion")]),
            Ct);

        var allowed = await Status(world, "1.1", "done");

        Assert.False(allowed.Refused, allowed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
    }

    [Fact]
    public async Task Done_is_refused_on_worker_evidence_and_allowed_on_an_orchestrator_run()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Worker);

        var refused = await Status(world, "1.1", "done");

        Assert.True(refused.Refused);
        Assert.Contains("run_by=orchestrator", refused.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(refused.Text);

        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        var allowed = await Status(world, "1.1", "done");

        Assert.False(allowed.Refused, allowed.Text);
    }

    [Fact]
    public async Task Done_is_refused_with_an_open_child_and_allowed_once_the_child_closes()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        var child = await WriteSeed.ChildAsync(world, world.TaskId, "1.1a", "subtask");

        var refused = await Status(world, "1.1", "done");

        Assert.True(refused.Refused);
        Assert.Contains("1.1a", refused.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(refused.Text);

        await WriteSeed.StatusAsync(world, child, ItemStatus.Done);

        var allowed = await Status(world, "1.1", "done");

        Assert.False(allowed.Refused, allowed.Text);
    }

    [Fact]
    public async Task Done_is_refused_with_an_open_blocker_finding_and_allowed_once_it_is_deferred()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        await WriteSeed.ChildAsync(world, world.TaskId, "F-07", "finding", severity: "blocker");

        var refused = await Status(world, "1.1", "done");

        Assert.True(refused.Refused);
        Assert.Contains("F-07 (BLOCKER)", refused.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(refused.Text);

        // The refusal's own second route, called verbatim.
        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId,
            "F-07",
            "deferred",
            WriteSeed.Orchestrator,
            new StatusArgs(Reason: "not this run", Target: "backlog"),
            Ct);

        Assert.False(moved.Refused, moved.Text);

        var allowed = await Status(world, "1.1", "done");

        Assert.False(allowed.Refused, allowed.Text);
    }

    [Fact]
    public async Task Ready_is_refused_while_a_predecessor_is_open_and_allowed_the_moment_it_closes()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var blocker = await WriteSeed.ChildAsync(world, world.PhaseId, "1.0", "task");
        await WriteSeed.LinkAsync(world, blocker, world.TaskId, LinkType.Blocks);

        var refused = await Status(world, "1.1", "ready");

        Assert.True(refused.Refused);
        Assert.Contains("1.0 blocks 1.1", refused.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));

        await WriteSeed.StatusAsync(world, blocker, ItemStatus.Done);

        var allowed = await Status(world, "1.1", "ready");

        Assert.False(allowed.Refused, allowed.Text);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    public async Task A_state_nobody_can_read_is_refused_until_it_carries_a_reason(string move)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);

        var refused = await Status(world, "1.1", move);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);

        var allowed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", move, WriteSeed.Orchestrator, new StatusArgs(Reason: "the why"), Ct);

        Assert.False(allowed.Refused, allowed.Text);
    }

    /// <summary>
    /// doc 07 §3. A <c>force</c> that silently succeeds is worse than no override at all, so the
    /// permissive half of this pair is not just "it closed" — it is "and work_audit says so".
    /// </summary>
    [Fact]
    public async Task Force_needs_a_reason_and_leaves_a_mark_work_audit_reports()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "never checked", ticked: false);

        var reasonless = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(Force: true), Ct);

        Assert.True(reasonless.Refused);
        RefusalShape.SelfCorrecting(reasonless.Text);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));

        var forced = await world.Writer.StatusAsync(
            world.WorkspaceId,
            "1.1",
            "done",
            WriteSeed.Orchestrator,
            new StatusArgs(Reason: "blocked at 3am on an unrelated outage", Force: true),
            Ct);

        Assert.False(forced.Refused, forced.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
        Assert.Contains("FORCED", forced.Text, StringComparison.Ordinal);

        Assert.Contains("forced_transition", await WriteSeed.EventKindsAsync(world, world.TaskId));

        var audit = await world.Reader.AuditAsync(await world.ScopeAsync("run-a"), Ct);

        Assert.Contains("forced-closure", audit, StringComparison.Ordinal);
        Assert.Contains("blocked at 3am", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_close_leaves_no_trace_of_itself()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);

        var refused = await Status(world, "1.1", "done");

        Assert.True(refused.Refused);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
        Assert.DoesNotContain("status", await WriteSeed.EventKindsAsync(world, world.TaskId));
    }

    [Fact]
    public async Task A_commit_argument_satisfies_the_artifact_condition_in_the_same_call()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(Commit: "a1b2c3d"), Ct);

        Assert.False(closed.Refused, closed.Text);

        await using var db = fixture.NewContext();
        Assert.Contains(
            db.Artifacts.Where(a => a.ItemId == world.TaskId && a.Kind == "commit"),
            a => a.Value == "a1b2c3d");
    }

    [Fact]
    public async Task Every_refusal_the_status_guard_can_emit_is_self_correcting()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "unchecked", ticked: false);

        (string Ref, string To, StatusArgs Args)[] calls =
        [
            ("1.1", "finished", None),
            ("no-such-ref", "done", None),
            ("1.1", "done", None),
            ("1.1", "review", None),
            ("1.1", "deferred", None),
            ("1.1", "deferred", new StatusArgs(Reason: "why", Target: "nowhere-at-all")),
            ("1.1", "superseded", None),
            ("1.1", "blocked", None),
        ];

        foreach (var (reference, to, args) in calls)
        {
            var result = await world.Writer.StatusAsync(
                world.WorkspaceId, reference, to, WriteSeed.Orchestrator, args, Ct);

            Assert.True(result.Refused, $"{reference} → {to} should have been refused:\n{result.Text}");
            RefusalShape.SelfCorrecting(result.Text);
        }
    }

    private static Task<WriteResult> Status(WriteWorld world, string reference, string to)
        => world.Writer.StatusAsync(world.WorkspaceId, reference, to, WriteSeed.Orchestrator, None, Ct);
}

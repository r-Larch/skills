using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 01 §3.4 and doc 07 §3 condition 2. The load-bearing property here is that a worker's
/// evidence is recorded as a worker's whatever the call says — which is what lets a worker hold
/// <c>work_verify</c> at all without being able to close its own task.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class EvidenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_worker_token_records_run_by_worker_even_when_the_argument_says_orchestrator()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var recorded = await world.Writer.VerifyAsync(
            world.WorkspaceId, "1.1", WriteSeed.Worker,
            "dotnet test --filter Guard", "Passed! Failed: 0, Passed: 91", true,
            "orchestrator", null, null, Ct);

        Assert.False(recorded.Refused, recorded.Text);

        await using var db = fixture.NewContext();
        var verification = await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct);

        Assert.Equal(VerifyRunner.Worker, verification.RunBy);

        // And it therefore cannot close the item it belongs to.
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused);
        Assert.Contains("run_by=orchestrator", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_orchestrator_token_records_what_it_says_it_ran()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await world.Writer.VerifyAsync(
            world.WorkspaceId, "1.1", WriteSeed.Orchestrator,
            "dotnet test --filter Guard", "Passed! Failed: 0, Passed: 91", true, null, null, null, Ct);

        await using var db = fixture.NewContext();
        var verification = await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct);

        Assert.Equal(VerifyRunner.Orchestrator, verification.RunBy);
    }

    [Fact]
    public async Task A_verification_without_the_line_the_command_printed_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.VerifyAsync(
            world.WorkspaceId, "1.1", WriteSeed.Orchestrator, "dotnet test", null, true, null, null, null, Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("exit code", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verification_with_no_command_falls_back_to_the_item_s_own_verify_cmd()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await world.Writer.VerifyAsync(
            world.WorkspaceId, "1.1", WriteSeed.Orchestrator, null, "Passed! 91", true, null, null, null, Ct);

        await using var db = fixture.NewContext();
        var verification = await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct);

        Assert.Equal("dotnet test --filter Guard", verification.Command);
    }

    [Fact]
    public async Task A_report_creates_the_attempt_and_an_assumption_row_per_line_of_assumptions()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var reported = await world.Writer.ReportAsync(
            world.WorkspaceId,
            "1.1",
            WriteSeed.Worker,
            new ReportArgs(
                "partial",
                Files: ["mcp/WorkState.Core/Writing/WorkWriter.cs"],
                Verify: "dotnet test --filter Guard → Passed! 91",
                Assumptions: ["Docker can run postgres:17 on this machine", "the sweep is cheap enough to run per call"],
                NotDone: "work_patch"),
            Ct);

        Assert.False(reported.Refused, reported.Text);
        Assert.Contains("a report is a claim, not evidence", reported.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();

        var attempt = await db.Attempts.SingleAsync(a => a.ItemId == world.TaskId, Ct);
        Assert.Equal(1, attempt.N);

        Assert.Equal(1, await db.Reports.CountAsync(r => r.AttemptId == attempt.Id, Ct));
        Assert.Equal(2, await db.Assumptions.CountAsync(
            a => a.WorkspaceId == world.WorkspaceId && a.ScopeItemId == world.TaskId, Ct));
    }

    [Fact]
    public async Task A_report_without_a_status_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.ReportAsync(
            world.WorkspaceId, "1.1", WriteSeed.Worker, new ReportArgs(string.Empty), Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
    }

    /// <summary>
    /// The carried-in question from U2c: the schema permits a resolved assumption to keep a reversal
    /// cost. The resolve transition is the only writer of that state, so it clears it — a settled
    /// question has no cost of being wrong, and a non-null reversal on a resolved row now means
    /// "imported", not "ambiguous".
    /// </summary>
    [Fact]
    public async Task Resolving_an_assumption_clears_its_reversal_cost()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var opened = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1",
            "pgvector is available in postgres:17", "reasoned", "high", "1.1", null, null, Ct);

        Assert.False(opened.Refused, opened.Text);

        var closed = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "A1", null, "the image has it", null, null,
            "confirmed", null, Ct);

        Assert.False(closed.Refused, closed.Text);

        await using var db = fixture.NewContext();
        var assumption = await db.Assumptions.SingleAsync(a => a.WorkspaceId == world.WorkspaceId, Ct);

        Assert.Equal(AssumptionState.Confirmed, assumption.State);
        Assert.Null(assumption.Reversal);
        Assert.NotNull(assumption.ResolvedAt);
    }

    [Fact]
    public async Task Every_refusal_the_evidence_and_thread_tools_emit_is_self_correcting()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var writer = world.Writer;
        var id = world.WorkspaceId;

        WriteResult[] refusals =
        [
            await writer.VerifyAsync(id, "1.1", WriteSeed.Orchestrator, null, null, true, null, null, null, Ct),
            await writer.VerifyAsync(id, "nope", WriteSeed.Orchestrator, null, "x", true, null, null, null, Ct),
            await writer.ReportAsync(id, "1.1", WriteSeed.Worker, new ReportArgs(string.Empty), Ct),
            await writer.NoteAsync(id, WriteSeed.Worker, "carry_forward", null, null, null, Ct),
            await writer.NoteAsync(id, WriteSeed.Worker, "not-a-kind", "body", null, null, Ct),
            await writer.NoteAsync(id, WriteSeed.Worker, "correction", "body", "no-such-ref", null, Ct),
            await writer.AssumeAsync(id, WriteSeed.Orchestrator, "1.1", null, null, null, null, null, null, Ct),
            await writer.AssumeAsync(id, WriteSeed.Orchestrator, "1.1", "s", "guessed", "high", null, null, null, Ct),
            await writer.AssumeAsync(id, WriteSeed.Orchestrator, "1.1", "s", "reasoned", "colossal", null, null, null, Ct),
            await writer.AssumeAsync(id, WriteSeed.Orchestrator, "A9", null, null, null, null, "confirmed", null, Ct),
            await writer.FindingAsync(id, WriteSeed.Orchestrator, [], Ct),
            await writer.FindingAsync(id, WriteSeed.Orchestrator, [new NewFinding("no-such-ref", "x")], Ct),
        ];

        foreach (var refusal in refusals)
        {
            Assert.True(refusal.Refused, $"should have been refused:\n{refusal.Text}");
            RefusalShape.SelfCorrecting(refusal.Text);
        }
    }

    [Fact]
    public async Task A_blocker_finding_recorded_through_work_finding_blocks_its_target()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        var recorded = await world.Writer.FindingAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            [new NewFinding("1.1", "ContentHash written before the work", "blocker", "review")],
            Ct);

        Assert.False(recorded.Refused, recorded.Text);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused);
        Assert.Contains("(BLOCKER)", refused.Text, StringComparison.Ordinal);

        // A minor finding does not: the guard has to leave honest work closeable.
        await world.Writer.StatusAsync(
            world.WorkspaceId, "F-01", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        await world.Writer.FindingAsync(
            world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewFinding("1.1", "a nit", "minor", "review")], Ct);

        var allowed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.False(allowed.Refused, allowed.Text);
    }

    [Fact]
    public async Task A_note_addressed_to_an_item_is_delivered_once_and_then_marked_consumed()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var written = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "do_not_rederive",
            "truncate facet values to 512 before insert", "1.1", null, Ct);

        Assert.False(written.Refused, written.Text);

        var scope = await world.ScopeAsync("1.1");
        var brief = await world.Reader.BriefSessionAsync(scope, "worker", WriteSeed.Keeper(fixture), Ct);

        Assert.Contains("truncate facet values", brief, StringComparison.Ordinal);

        var again = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "worker", Ct);

        Assert.DoesNotContain("truncate facet values", again, StringComparison.Ordinal);
    }
}

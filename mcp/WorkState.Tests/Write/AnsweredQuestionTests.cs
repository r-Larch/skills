using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// m64 answered-not-unverified: a question closes with its answer, in <c>reason</c>. The one call
/// records it as a passed human verification and a decision note, so it reads as answered rather
/// than as done without proof; without an answer it is refused with the call that closes it.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AnsweredQuestionTests(PostgresFixture fixture)
{
    private const string Answer = "Keep the bare ref in the first column; René, 2026-10-29";

    [Fact]
    public async Task AnsweredQuestion_records_a_human_verification_and_a_decision_note_and_is_not_done_unverified()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var question = await WriteSeed.ChildAsync(world, world.PhaseId, "Q1", kind: "question", status: ItemStatus.InProgress);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "Q1", "done", WriteSeed.Orchestrator, new StatusArgs(Reason: Answer), Ct);

        Assert.False(closed.Refused, closed.Text);
        Assert.Contains("answer", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("Q1"));

        await using var db = fixture.NewContext();
        var verification = await db.Verifications.SingleAsync(v => v.ItemId == question, Ct);
        Assert.True(verification.Passed);
        Assert.Equal(VerifyRunner.Human, verification.RunBy);
        Assert.Equal(Answer, verification.ResultLine);

        var note = await db.Notes.SingleAsync(n => n.FromItemId == question, Ct);
        Assert.Equal(NoteKind.Decision, note.Kind);
        Assert.Equal(Answer, note.Body);

        var board = new BoardQueries(fixture.DataSource);
        Assert.DoesNotContain(await board.DoneUnverifiedAsync(world.RunId, Ct), u => u.Id == question);

        var attention = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);
        Assert.DoesNotContain(attention, l => l.Rank == AttentionRank.DoneUnverified && l.Ref == "Q1");
    }

    [Fact]
    public async Task AnsweredQuestion_without_an_answer_is_refused_with_the_call_that_closes_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var question = await WriteSeed.ChildAsync(world, world.PhaseId, "Q1", kind: "question", status: ItemStatus.InProgress);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "Q1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("work_status(ref:\"Q1\", to:\"done\", reason:\"<the answer>\")", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("Q1"));

        await using var db = fixture.NewContext();
        Assert.False(await db.Verifications.AnyAsync(v => v.ItemId == question, Ct));
    }

    [Fact]
    public async Task AnsweredQuestion_leaves_a_tasks_done_guard_unchanged()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains("run_by=orchestrator", refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("answer", refused.Text, StringComparison.Ordinal);

        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(Reason: "shipped"), Ct);

        Assert.False(closed.Refused, closed.Text);
        Assert.DoesNotContain("answer", closed.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Equal(1, await db.Verifications.CountAsync(v => v.ItemId == world.TaskId, Ct));
        Assert.False(await db.Notes.AnyAsync(n => n.FromItemId == world.TaskId, Ct));
    }
}

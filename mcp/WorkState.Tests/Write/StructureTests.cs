using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 01 §3.2. The one creation rule that is a guard — a task without a verification command or a
/// done-criterion — plus the rules that are <em>not</em> reimplemented here because Postgres owns
/// them, and are surfaced from its own message instead (doc 07 §1).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class StructureTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_task_without_a_verification_command_or_a_criterion_cannot_be_written()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.CreateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            [new NewItemArg("task", "Do the thing", Parent: "1")],
            Ct);

        Assert.True(refused.Refused);
        Assert.Equal(2, RefusalShape.Conditions(refused.Text));
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("RED before the work", refused.Text, StringComparison.Ordinal);

        var written = await world.Writer.CreateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            [
                new NewItemArg(
                    "task",
                    "Do the thing",
                    Parent: "1",
                    VerifyCmd: "dotnet test --filter Thing",
                    Criteria: ["the thing is surfaced on the board"]),
            ],
            Ct);

        Assert.False(written.Refused, written.Text);

        await using var db = fixture.NewContext();
        var created = await db.Items.SingleAsync(
            i => i.WorkspaceId == world.WorkspaceId && i.Title == "Do the thing", Ct);

        Assert.Equal(world.PhaseId, created.ParentId);
        Assert.Equal(1, await db.ItemCriteria.CountAsync(c => c.ItemId == created.Id, Ct));
    }

    [Fact]
    public async Task A_phase_needs_neither_because_nothing_verifies_a_container()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var written = await world.Writer.CreateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            [new NewItemArg("phase", "Query surface", Parent: "run-a")],
            Ct);

        Assert.False(written.Refused, written.Text);
    }

    [Fact]
    public async Task A_batch_creates_a_whole_decomposition_in_one_call()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var written = await world.Writer.CreateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            [
                new NewItemArg("task", "One", Parent: "1", VerifyCmd: "dotnet test --filter One", Criteria: ["a"]),
                new NewItemArg("task", "Two", Parent: "1", VerifyCmd: "dotnet test --filter Two", Criteria: ["b"]),
                new NewItemArg("task", "Three", Parent: "1", VerifyCmd: "dotnet test --filter Three", Criteria: ["c"]),
            ],
            Ct);

        Assert.False(written.Refused, written.Text);

        await using var db = fixture.NewContext();
        Assert.Equal(4, await db.Items.CountAsync(i => i.ParentId == world.PhaseId, Ct));
    }

    /// <summary>
    /// doc 04 §4 — an empty server tells the agent to call <c>work_create</c>, so that call has to
    /// work before a workspace exists, or the onboarding names a route that refuses.
    /// </summary>
    [Fact]
    public async Task The_route_onboarding_prints_works_on_a_server_with_nothing_in_it()
    {
        var slug = $"cold-{Guid.CreateVersion7():n}";
        var writer = WriteSeed.Writer(fixture);

        var workspaceId = await writer.EnsureWorkspaceAsync(slug, Ct);

        var written = await writer.CreateAsync(
            workspaceId,
            WriteSeed.Orchestrator,
            [new NewItemArg("project", "The repo", Ref: "gaia")],
            Ct);

        Assert.False(written.Refused, written.Text);
        Assert.Equal(workspaceId, await writer.EnsureWorkspaceAsync(slug, Ct));

        await using var db = fixture.NewContext();
        Assert.True(await db.Items.AnyAsync(i => i.WorkspaceId == workspaceId && i.Ref == "gaia", Ct));
    }

    [Fact]
    public async Task An_unknown_kind_is_refused_with_the_ladder_that_exists()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.CreateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, [new NewItemArg("epic", "Nope", Parent: "1")], Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("phase", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_expected_revision_is_reported_rather_than_overwritten()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.UpdateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(Title: "x", ExpectedRevision: 99), Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("expected_revision 99", refused.Text, StringComparison.Ordinal);

        var written = await world.Writer.UpdateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(Title: "x", ExpectedRevision: 1), Ct);

        Assert.False(written.Refused, written.Text);
    }

    [Fact]
    public async Task An_appended_criterion_lands_next_and_a_numbered_one_is_checked_off()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "the first", ticked: false);

        await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "1.1",
            new UpdateArgs(Criteria:
            [
                new CriterionArg("c1", Checked: true, Evidence: "the assertion"),
                new CriterionArg(Text: "the second"),
            ]),
            Ct);

        await using var db = fixture.NewContext();
        var criteria = await db.ItemCriteria
            .Where(c => c.ItemId == world.TaskId).OrderBy(c => c.Seq).ToListAsync(Ct);

        Assert.Equal(2, criteria.Count);
        Assert.True(criteria[0].Checked);
        Assert.Equal("the assertion", criteria[0].Evidence);
        Assert.Equal("the second", criteria[1].Text);
        Assert.False(criteria[1].Checked);
    }

    /// <summary>
    /// doc 07 §1 — a blocks cycle is a constraint, not a guard. This asserts the constraint's own
    /// message reaches the agent rather than being paraphrased into something that can drift.
    /// </summary>
    [Fact]
    public async Task A_blocks_cycle_is_refused_by_the_database_and_its_message_reaches_the_agent()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        var forward = await world.Writer.LinkAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", "1.2", "blocks", false, Ct);

        Assert.False(forward.Refused, forward.Text);

        var cycle = await world.Writer.LinkAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.2", "1.1", "blocks", false, Ct);

        Assert.True(cycle.Refused);
        RefusalShape.SelfCorrecting(cycle.Text);
        Assert.Contains("blocks cycle refused", cycle.Text, StringComparison.Ordinal);
        Assert.Contains("derived_from", cycle.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_dependency_can_be_removed_which_is_what_the_ready_refusal_promises()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var blocker = await WriteSeed.ChildAsync(world, world.PhaseId, "1.0");
        await WriteSeed.LinkAsync(world, blocker, world.TaskId, LinkType.Blocks);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "ready", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused);
        Assert.Contains("remove:true", refused.Text, StringComparison.Ordinal);

        var removed = await world.Writer.LinkAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.0", "1.1", "blocks", true, Ct);

        Assert.False(removed.Refused, removed.Text);

        var allowed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "ready", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.False(allowed.Refused, allowed.Text);
    }

    [Fact]
    public async Task Supersedes_closes_the_item_it_replaces_in_the_same_transaction()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        var linked = await world.Writer.LinkAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.2", "1.1", "supersedes", false, Ct);

        Assert.False(linked.Refused, linked.Text);
        Assert.Equal(ItemStatus.Superseded, await world.StatusAsync("1.1"));
    }

    [Fact]
    public async Task Moving_an_item_between_two_siblings_rewrites_one_row()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");

        var moved = await world.Writer.MoveAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.3", null, "1.2", null, Ct);

        Assert.False(moved.Refused, moved.Text);

        var first = await world.ItemAsync("1.1");
        var second = await world.ItemAsync("1.2");
        var third = await world.ItemAsync("1.3");

        Assert.Equal(world.PhaseId, third.ParentId);
        Assert.True(third.Seq < second.Seq, $"{third.Seq} should sort before {second.Seq}");
        Assert.True(third.Seq > first.Seq, $"{third.Seq} should sort after {first.Seq}");
    }

    [Fact]
    public async Task Every_refusal_the_structure_tools_emit_is_self_correcting()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var writer = world.Writer;
        var id = world.WorkspaceId;

        WriteResult[] refusals =
        [
            await writer.CreateAsync(id, WriteSeed.Orchestrator, [], Ct),
            await writer.CreateAsync(id, WriteSeed.Orchestrator, [new NewItemArg("task", "x", Parent: "1")], Ct),
            await writer.CreateAsync(id, WriteSeed.Orchestrator, [new NewItemArg("task", "")], Ct),
            await writer.CreateAsync(id, WriteSeed.Orchestrator, [new NewItemArg("phase", "x")], Ct),
            await writer.CreateAsync(id, WriteSeed.Orchestrator, [new NewItemArg("phase", "x", Parent: "nope")], Ct),
            await writer.UpdateAsync(id, WriteSeed.Orchestrator, "nope", new UpdateArgs(Title: "x"), Ct),
            await writer.MoveAsync(id, WriteSeed.Orchestrator, "1.1", null, null, null, Ct),
            await writer.MoveAsync(id, WriteSeed.Orchestrator, "1.1", "nope", null, null, Ct),
            await writer.LinkAsync(id, WriteSeed.Orchestrator, "1.1", "1", "chains", false, Ct),
            await writer.LinkAsync(id, WriteSeed.Orchestrator, "nope", "1", "blocks", false, Ct),
            await writer.LinkAsync(id, WriteSeed.Orchestrator, "1.1", "1", "blocks", true, Ct),
        ];

        foreach (var refusal in refusals)
        {
            Assert.True(refusal.Refused, $"should have been refused:\n{refusal.Text}");
            RefusalShape.SelfCorrecting(refusal.Text);
        }
    }
}

using WorkState.Core.Domain;
using WorkState.Core.Queries;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

[Collection(StoreCollection.Name)]
public sealed class QueryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_frontier_excludes_an_item_whose_blocker_is_open_and_includes_it_once_done()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        var blocker = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "f1", 1m);
        var blocked = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "f2", 2m);

        await using (var context = fixture.NewContext())
        {
            context.Items.AddRange(blocker, blocked);
            await context.SaveChangesAsync(Ct);

            context.ItemLinks.Add(new ItemLink
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                FromItemId = blocker.Id,
                ToItemId = blocked.Id,
                Type = LinkType.Blocks,
            });
            await context.SaveChangesAsync(Ct);
        }

        var beforeClosing = await queries.ReadyFrontierAsync(world.RunA, Ct);
        Assert.Contains(beforeClosing, r => r.Id == blocker.Id);
        Assert.DoesNotContain(beforeClosing, r => r.Id == blocked.Id);

        await using (var context = fixture.NewContext())
        {
            var toClose = await context.Items.FindAsync([blocker.Id], Ct);
            toClose!.Status = ItemStatus.Done;
            toClose.Resolution = ItemResolution.Accepted;
            toClose.ClosedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        var afterClosing = await queries.ReadyFrontierAsync(world.RunA, Ct);
        Assert.Contains(afterClosing, r => r.Id == blocked.Id);
        Assert.DoesNotContain(afterClosing, r => r.Id == blocker.Id);
    }

    [Fact]
    public async Task The_frontier_excludes_a_parent_that_still_has_open_children()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "g1", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        var child = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "g1.1", 1m);
        context.Items.Add(child);
        await context.SaveChangesAsync(Ct);

        var frontier = await queries.ReadyFrontierAsync(world.RunA, Ct);

        Assert.Contains(frontier, r => r.Id == child.Id);
        Assert.DoesNotContain(frontier, r => r.Id == phase.Id);
    }

    [Fact]
    public async Task The_frontier_orders_by_priority_then_seq()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "h", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        var late = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "h3", 3m);
        var first = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "h1", 1m);
        var urgent = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "h2", 2m);
        urgent.Priority = 5;
        context.Items.AddRange(late, first, urgent);
        await context.SaveChangesAsync(Ct);

        var frontier = await queries.ReadyFrontierAsync(phase.Id, Ct);

        Assert.Equal(["h2", "h1", "h3"], frontier.Select(r => r.Ref));
    }

    /// <summary>
    /// The defect finding #4 of the corpus import named, and the one shape the old fixtures could not
    /// express: <c>seq</c> orders siblings, so both phases' first task is <c>seq = 1</c> and ordering a
    /// whole run by it leaves the order between phases to the planner. Two phases is the minimum that
    /// tells the two orderings apart.
    /// </summary>
    [Fact]
    public async Task The_frontier_orders_across_phases_by_the_document_path_not_by_seq()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var phaseOne = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "1", 1m);
        var phaseTwo = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "2", 2m);
        context.Items.AddRange(phaseOne, phaseTwo);
        await context.SaveChangesAsync(Ct);

        // Inserted back to front, so a query that falls back on insertion order fails too.
        context.Items.AddRange(
            Seed.NewItem(world.WorkspaceId, phaseTwo.Id, "task", "2.2", 2m),
            Seed.NewItem(world.WorkspaceId, phaseTwo.Id, "task", "2.1", 1m),
            Seed.NewItem(world.WorkspaceId, phaseOne.Id, "task", "1.2", 2m),
            Seed.NewItem(world.WorkspaceId, phaseOne.Id, "task", "1.1", 1m));
        await context.SaveChangesAsync(Ct);

        var frontier = await queries.ReadyFrontierAsync(world.RunA, Ct);

        Assert.Equal(["1.1", "1.2", "2.1", "2.2"], frontier.Select(r => r.Ref));
    }

    [Fact]
    public async Task Priority_still_outranks_the_document_path_across_phases()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var phaseOne = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "p1", 1m);
        var phaseTwo = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "p2", 2m);
        context.Items.AddRange(phaseOne, phaseTwo);
        await context.SaveChangesAsync(Ct);

        var urgent = Seed.NewItem(world.WorkspaceId, phaseTwo.Id, "task", "p2.2", 2m);
        urgent.Priority = 5;
        context.Items.AddRange(
            Seed.NewItem(world.WorkspaceId, phaseOne.Id, "task", "p1.1", 1m),
            Seed.NewItem(world.WorkspaceId, phaseOne.Id, "task", "p1.2", 2m),
            Seed.NewItem(world.WorkspaceId, phaseTwo.Id, "task", "p2.1", 1m),
            urgent);
        await context.SaveChangesAsync(Ct);

        var frontier = await queries.ReadyFrontierAsync(world.RunA, Ct);

        Assert.Equal(["p2.2", "p1.1", "p1.2", "p2.1"], frontier.Select(r => r.Ref));
    }

    [Fact]
    public async Task Unclosed_dependencies_flag_an_abandoned_blocker_and_an_ordering_inversion()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var abandoned = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "u1", 1m);
        abandoned.Status = ItemStatus.Cancelled;
        abandoned.ClosedAt = DateTimeOffset.UtcNow;

        var waitingOnAbandoned = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "u2", 2m);
        var early = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "u3", 3m);
        var laterBlocker = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "u4", 4m);
        var healthyBlocker = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "u5", 5m);
        var healthyWaiter = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "u6", 6m);

        context.Items.AddRange(abandoned, waitingOnAbandoned, early, laterBlocker, healthyBlocker, healthyWaiter);
        await context.SaveChangesAsync(Ct);

        context.ItemLinks.AddRange(
            Blocks(world.WorkspaceId, abandoned.Id, waitingOnAbandoned.Id),
            Blocks(world.WorkspaceId, laterBlocker.Id, early.Id),
            Blocks(world.WorkspaceId, healthyBlocker.Id, healthyWaiter.Id));
        await context.SaveChangesAsync(Ct);

        var unclosed = await queries.UnclosedDependenciesAsync(world.RunA, Ct);

        Assert.Contains(unclosed, d => d.Waiting == "u2" && d.On == "u1" && d.Status == ItemStatus.Cancelled);
        Assert.Contains(unclosed, d => d.Waiting == "u3" && d.On == "u4" && !d.OrderedCorrectly);
        Assert.DoesNotContain(unclosed, d => d.Waiting == "u6");
    }

    [Fact]
    public async Task The_rollup_counts_the_whole_subtree_not_just_the_children()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "j", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        var task = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "j1", 1m);
        context.Items.Add(task);
        await context.SaveChangesAsync(Ct);

        var openSub = Seed.NewItem(world.WorkspaceId, task.Id, "subtask", "j1.a", 1m);
        var doneSub = Seed.NewItem(world.WorkspaceId, task.Id, "subtask", "j1.b", 2m);
        doneSub.Status = ItemStatus.Done;
        doneSub.Resolution = ItemResolution.Accepted;
        doneSub.ClosedAt = DateTimeOffset.UtcNow;
        context.Items.AddRange(openSub, doneSub);
        await context.SaveChangesAsync(Ct);

        var rollup = await queries.RollupAsync(phase.Id, Ct);

        Assert.NotNull(rollup);
        Assert.Equal(2, rollup.OpenCount);
        Assert.Equal(1, rollup.DoneCount);
    }

    [Fact]
    public async Task The_subtree_comes_back_in_document_order()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var phaseOne = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "1", 1m);
        var phaseTwo = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "2", 2m);
        context.Items.AddRange(phaseOne, phaseTwo);
        await context.SaveChangesAsync(Ct);

        context.Items.AddRange(
            Seed.NewItem(world.WorkspaceId, phaseOne.Id, "task", "1.1", 1m),
            Seed.NewItem(world.WorkspaceId, phaseOne.Id, "task", "1.2", 2m),
            Seed.NewItem(world.WorkspaceId, phaseTwo.Id, "task", "2.1", 1m));
        await context.SaveChangesAsync(Ct);

        var nodes = await queries.SubtreeAsync(world.RunA, Ct);

        Assert.Equal(["run-a", "1", "1.1", "1.2", "2", "2.1"], nodes.Select(n => n.Ref));
    }

    [Fact]
    public async Task Open_threads_gather_assumptions_findings_followups_and_unconsumed_notes()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var finding = Seed.NewItem(world.WorkspaceId, world.RunA, "finding", "F-07", 1m, "the gate filter cannot go red");
        var followup = Seed.NewItem(world.WorkspaceId, world.RunA, "followup", "FU-1", 2m, "rebalance the seq precision");
        var closedFinding = Seed.NewItem(world.WorkspaceId, world.RunA, "finding", "F-08", 3m, "already handled");
        closedFinding.Status = ItemStatus.Done;
        closedFinding.Resolution = ItemResolution.Accepted;
        closedFinding.ClosedAt = DateTimeOffset.UtcNow;
        context.Items.AddRange(finding, followup, closedFinding);

        context.Assumptions.Add(new Assumption
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ScopeItemId = world.RunA,
            Ref = "A1",
            Statement = "Docker is usable from a background agent here.",
            Evidence = EvidenceLevel.Probed,
            Reversal = ReversalCost.Low,
        });
        context.Notes.Add(new Note
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            FromItemId = world.RunA,
            ToItemId = world.RunA,
            Kind = NoteKind.CarryForward,
            Body = "the probe settled the MCP surface; do not re-derive it",
        });
        await context.SaveChangesAsync(Ct);

        var threads = await queries.OpenThreadsAsync(world.WorkspaceId, world.RunA, Ct);
        var kinds = threads.Select(t => t.Type).ToHashSet();

        Assert.Equal(["assumption", "finding", "followup", "note"], kinds.Order());
        Assert.DoesNotContain(threads, t => t.Ref == "F-08");
    }

    [Fact]
    public async Task Search_finds_an_item_by_intent_and_never_a_doc_block()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var queries = new WorkStateQueries(fixture.DataSource);

        await using var context = fixture.NewContext();
        var task = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "q1", 1m, "Wire the frontier query");
        task.Intent = "The orchestrator needs a cheap answer to what is actionable right now.";

        var unrelated = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "q2", 2m, "Tidy the Dockerfile");
        context.Items.AddRange(task, unrelated);

        var doc = new Doc
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ItemId = world.RunA,
            Slug = "conventions",
            Title = "Conventions",
        };
        context.Docs.Add(doc);
        context.DocBlocks.Add(new DocBlock
        {
            Id = Guid.CreateVersion7(),
            DocId = doc.Id,
            Seq = 1m,
            ShortId = "b3",
            Heading = "Verification",
            Body = "Read the actual result line, not an exit code.",
        });
        await context.SaveChangesAsync(Ct);

        var hits = await queries.SearchAsync(world.WorkspaceId, "actionable", cancellationToken: Ct);
        var hit = Assert.Single(hits);
        Assert.Equal("item", hit.Source);
        Assert.Equal("q1", hit.Ref);

        var blockHits = await queries.SearchAsync(world.WorkspaceId, "exit code", cancellationToken: Ct);
        Assert.Empty(blockHits);
    }

    private static ItemLink Blocks(Guid workspaceId, Guid from, Guid to) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = workspaceId,
        FromItemId = from,
        ToItemId = to,
        Type = LinkType.Blocks,
    };
}

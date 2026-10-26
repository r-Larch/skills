using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// The two behaviours doc 06 leans hardest on: the frontier only offers work that is actually
/// unblocked, and the attention block is silent when there is nothing to say and actionable when
/// there is. Both are seeded by hand, because both need a state the corpus fixture does not have.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AttentionAndFrontierTests(PostgresFixture fixture)
{
    [Fact]
    public async Task work_next_excludes_an_item_whose_blocker_is_open_and_includes_it_once_the_blocker_is_done()
    {
        var world = await SeedAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var before = await ReadServer.CallAsync(
            client, "work_next", new Dictionary<string, object?> { ["scope"] = "run-x" }, Ct);

        Assert.Contains("t1", before, StringComparison.Ordinal);
        Assert.DoesNotContain("t2", before, StringComparison.Ordinal);

        await CloseAsync(world.T1);

        var after = await ReadServer.CallAsync(
            client, "work_next", new Dictionary<string, object?> { ["scope"] = "run-x" }, Ct);

        Assert.Contains("t2", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_result_emits_no_attention_header_at_all()
    {
        var world = await SeedAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        foreach (var tool in (string[])["work_brief", "work_next", "work_tree", "work_threads"])
        {
            var text = await ReadServer.CallAsync(
                client, tool, new Dictionary<string, object?> { ["scope"] = "run-x" }, Ct);

            Assert.DoesNotContain("--- attention", text, StringComparison.Ordinal);
            Assert.DoesNotContain("⚠", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_dirty_result_emits_at_most_five_attention_lines_and_every_one_names_a_call()
    {
        var world = await SeedAsync();
        await DirtyAsync(world);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var text = await ReadServer.CallAsync(
            client, "work_brief", new Dictionary<string, object?> { ["scope"] = "run-x" }, Ct);

        var block = text[text.IndexOf("--- attention", StringComparison.Ordinal)..].Split('\n');

        Assert.Matches(@"^--- attention \([1-5]( of \d+)?\) ---$", block[0]);
        var warned = block[1..].Where(l => l.StartsWith('⚠')).ToArray();
        Assert.InRange(warned.Length, 1, 5);
        Assert.All(block[1..].Except(warned), line => Assert.Matches(@"^\+\d+ more — read work://attention/run-x with ReadMcpResourceTool$", line));
        Assert.All(warned, line =>
        {
            Assert.Contains(" — ", line, StringComparison.Ordinal);
            Assert.Matches(@"— work_[a-z]+\(", line);
        });

        // The most expensive thing to ignore is a lease nobody is holding (doc 06 §5 ranking).
        Assert.Contains("claim", block[1], StringComparison.Ordinal);
    }

    private sealed record World(string Slug, Guid WorkspaceId, Guid Run, Guid T1, Guid T2, Guid T3);

    private async Task<World> SeedAsync()
    {
        var slug = $"att-{Guid.CreateVersion7():n}";
        await using var db = fixture.NewContext();

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = slug };
        db.Workspaces.Add(workspace);

        var project = Seed.NewItem(workspace.Id, null, "project", "proj-x", 1m, "Project X");
        db.Items.Add(project);
        await db.SaveChangesAsync(Ct);

        var run = Seed.NewItem(workspace.Id, project.Id, "run", "run-x", 1m, "Run X");
        run.Intent = "Prove the frontier offers only unblocked work.";
        db.Items.Add(run);
        await db.SaveChangesAsync(Ct);

        var phase = Seed.NewItem(workspace.Id, run.Id, "phase", "p1", 1m, "Phase one");
        db.Items.Add(phase);
        await db.SaveChangesAsync(Ct);

        var t1 = Seed.NewItem(workspace.Id, phase.Id, "task", "t1", 1m, "First task");
        var t2 = Seed.NewItem(workspace.Id, phase.Id, "task", "t2", 2m, "Second task");
        var t3 = Seed.NewItem(workspace.Id, phase.Id, "task", "t3", 3m, "Third task");
        t1.VerifyCmd = "dotnet test --filter One";
        t2.VerifyCmd = "dotnet test --filter Two";
        t3.VerifyCmd = "dotnet test --filter Three";
        db.Items.AddRange(t1, t2, t3);
        await db.SaveChangesAsync(Ct);

        // t3 exists so the tree has something that is neither ready nor blocked; it becomes the
        // stale in_progress row in the dirty case.
        db.ItemLinks.Add(new ItemLink
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            FromItemId = t1.Id,
            ToItemId = t2.Id,
            Type = LinkType.Blocks,
        });
        db.ItemLinks.Add(new ItemLink
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            FromItemId = t1.Id,
            ToItemId = t3.Id,
            Type = LinkType.Blocks,
        });
        await db.SaveChangesAsync(Ct);

        return new World(slug, workspace.Id, run.Id, t1.Id, t2.Id, t3.Id);
    }

    private async Task CloseAsync(Guid itemId)
    {
        await using var db = fixture.NewContext();
        var item = await db.Items.SingleAsync(i => i.Id == itemId, Ct);
        item.Status = ItemStatus.Done;
        item.Resolution = ItemResolution.Accepted;
        item.ClosedAt = DateTimeOffset.UtcNow;
        item.ClosedBy = "test";
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>All six attention sources at once, so the cap and the ranking both have to hold.</summary>
    private async Task DirtyAsync(World world)
    {
        await CloseAsync(world.T1);

        await using var db = fixture.NewContext();

        var t3 = await db.Items.SingleAsync(i => i.Id == world.T3, Ct);
        t3.Status = ItemStatus.InProgress;
        t3.StartedAt = DateTimeOffset.UtcNow.AddDays(-1);

        db.Claims.Add(new Claim
        {
            Id = Guid.CreateVersion7(),
            ItemId = world.T2,
            SerializationKey = world.Slug,
            Writes = true,
            AgentName = "worker-1",
            Role = ActorRole.Worker,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-13),
        });

        var finding = Seed.NewItem(world.WorkspaceId, world.T2, "finding", "F-07", 1m, "ContentHash written early");
        finding.Severity = "blocker";
        finding.Source = "review";
        db.Items.Add(finding);

        db.Assumptions.Add(new Assumption
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ScopeItemId = world.Run,
            Ref = "A3",
            Statement = "The dev machine can reach the export endpoint",
            Evidence = EvidenceLevel.Reasoned,
            Reversal = ReversalCost.Medium,
            RevisitItemId = world.T1,
            State = AssumptionState.Open,
        });

        db.Notes.Add(new Note
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            FromItemId = world.Run,
            ToItemId = world.T2,
            Kind = NoteKind.CarryForward,
            Body = "truncate facet values to 512 before insert",
            CreatedBy = "test",
        });

        await db.SaveChangesAsync(Ct);
    }
}

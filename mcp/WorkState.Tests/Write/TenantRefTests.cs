using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// m47. A ref is unique per workspace (a tenant). A project or a run scopes its own refs, so the
/// rows that carry no ref scope are a feature with no parent and an assumption on no item; before
/// TenantRefs those collided with the same ref in every other workspace.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TenantRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Two_workspaces_each_hold_the_same_top_level_refs()
    {
        var (a, b) = (await WriteSeed.CreateAsync(fixture), await WriteSeed.CreateAsync(fixture));

        foreach (var world in new[] { a, b })
        {
            var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
                [new NewItemArg("project", "Project x", Ref: "x"), new NewItemArg("feature", "Feature f", Ref: "f")], Ct);
            Assert.False(created.Refused, created.Text);

            await WorkspaceAssumptionAsync(world, "A1");
        }

        await using var db = fixture.NewContext();
        foreach (var world in new[] { a, b })
        {
            Assert.Equal(2, await db.Items.CountAsync(
                i => i.WorkspaceId == world.WorkspaceId && (i.Ref == "x" || i.Ref == "f"), Ct));
            Assert.Equal(1, await db.Assumptions.CountAsync(
                s => s.WorkspaceId == world.WorkspaceId && s.ScopeItemId == null && s.Ref == "A1", Ct));
        }
    }

    [Fact]
    public async Task Inside_one_workspace_a_top_level_ref_stays_unique_and_numbering_ignores_other_workspaces()
    {
        var (a, b) = (await WriteSeed.CreateAsync(fixture), await WriteSeed.CreateAsync(fixture));

        Assert.False((await a.Writer.CreateAsync(a.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("feature", "Taken by hand", Ref: "feature-1")], Ct)).Refused);

        var dupe = await a.Writer.CreateAsync(a.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("feature", "The same ref again", Ref: "feature-1")], Ct);
        Assert.True(dupe.Refused, dupe.Text);

        Assert.Equal("feature-2", await DerivedFeatureAsync(a));
        Assert.Equal("feature-1", await DerivedFeatureAsync(b));

        await WorkspaceAssumptionAsync(a, "A1");
        var error = await Seed.RefusedAsync(() => WorkspaceAssumptionAsync(a, "A1"));
        Assert.Equal("assumption_ref_unique", error.ConstraintName);

        // An assumption on a parentless feature has no ref scope either, so it numbers among A1 above.
        Assert.Equal("A2", await AssumeOnAsync(a, "feature-1"));
        Assert.Equal("A1", await AssumeOnAsync(b, "feature-1"));
    }

    [Fact]
    public async Task A_ref_lookup_in_one_workspace_never_returns_another_workspaces_row()
    {
        var (a, b) = (await WriteSeed.CreateAsync(fixture), await WriteSeed.CreateAsync(fixture));
        var features = new Dictionary<Guid, Guid>();

        foreach (var world in new[] { a, b })
        {
            Assert.False((await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
                [new NewItemArg("project", "Project x", Ref: "x"), new NewItemArg("feature", "Feature f", Ref: "f")], Ct)).Refused);
            await WorkspaceAssumptionAsync(world, "A1");
            features[world.WorkspaceId] = (await world.ItemAsync("f")).Id;
        }

        var board = new BoardQueries(fixture.DataSource);
        foreach (var reference in new[] { "x", "f" })
        {
            var found = Assert.Single(await board.ResolveAsync(b.WorkspaceId, reference, Ct));
            Assert.Equal(b.WorkspaceId, found.WorkspaceId);
        }

        foreach (var reference in new[] { "A1", $"{BoardQueries.WorkspaceQualifier}/A1" })
        {
            Assert.Single(await board.AssumptionByRefAsync(b.WorkspaceId, reference, Ct));
        }

        // work_status → deferred resolves target: by ref, and numbers the follow-up inside f's scope.
        foreach (var world in new[] { a, b })
        {
            var moved = await world.Writer.StatusAsync(world.WorkspaceId, "1.1", "deferred", WriteSeed.Orchestrator,
                new StatusArgs(Reason: "f's problem", Target: "f"), Ct);
            Assert.False(moved.Refused, moved.Text);

            var carried = await world.ItemAsync("1.1-d");
            Assert.Equal(features[world.WorkspaceId], carried.ParentId);
        }

        // work_assume(resolve:) locks the open assumption it resolves by ref.
        var resolved = await b.Writer.AssumeAsync(
            b.WorkspaceId, WriteSeed.Orchestrator, "workspace/A1", null, "settled", null, null, "confirmed", null, Ct);
        Assert.False(resolved.Refused, resolved.Text);

        await using var db = fixture.NewContext();
        Assert.Equal(AssumptionState.Open, (await db.Assumptions.SingleAsync(s => s.WorkspaceId == a.WorkspaceId, Ct)).State);
        Assert.Equal(AssumptionState.Confirmed, (await db.Assumptions.SingleAsync(s => s.WorkspaceId == b.WorkspaceId, Ct)).State);
    }

    private static async Task<string> DerivedFeatureAsync(WriteWorld world)
    {
        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("feature", "Numbered by the server")], Ct);
        Assert.False(created.Refused, created.Text);

        await using var db = world.Fixture.NewContext();
        return (await db.Items
            .Where(i => i.WorkspaceId == world.WorkspaceId && i.Title == "Numbered by the server")
            .SingleAsync(Ct)).Ref;
    }

    private static async Task<string> AssumeOnAsync(WriteWorld world, string scope)
    {
        var opened = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, scope, "the feature ships alone", "reasoned", "low", null, null, null, Ct);
        Assert.False(opened.Refused, opened.Text);

        await using var db = world.Fixture.NewContext();
        return (await db.Assumptions
            .Where(s => s.WorkspaceId == world.WorkspaceId && s.Statement == "the feature ships alone")
            .SingleAsync(Ct)).Ref;
    }

    private async Task WorkspaceAssumptionAsync(WriteWorld world, string reference)
    {
        await using var db = fixture.NewContext();
        db.Assumptions.Add(new Assumption
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ScopeItemId = null,
            Ref = reference,
            Statement = "the tenant holds its own refs",
            Evidence = EvidenceLevel.Reasoned,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }
}

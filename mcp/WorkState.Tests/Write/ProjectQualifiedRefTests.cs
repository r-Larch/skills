using Microsoft.EntityFrameworkCore;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// project-qualified-ref. Seen live: <c>autowork/m5/13</c> was "no item has ref" while <c>m5/13</c>
/// resolved — the resolver took one qualifier. With projects sharing run names, a project may lead:
/// <c>project/run/ref</c> and <c>project/ref</c>, and a printed ref is the shortest form that
/// resolves to that item alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectQualifiedRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_project_run_ref_resolves_to_the_one_item_that_run_ref_cannot_single_out()
    {
        var w = await WorldAsync();

        Assert.Equal(2, (await ResolveAsync(w, "m5/13")).Count);
        Assert.Equal(2, (await ResolveAsync(w, "13")).Count);

        Assert.Equal(w.Autowork13, Assert.Single(await ResolveAsync(w, $"{w.Autowork}/m5/13")).Id);
        Assert.Equal(w.Nomos13, Assert.Single(await ResolveAsync(w, $"{w.Nomos}/M5/13")).Id);

        var tool = Assert.IsType<ScopeResult.Found>(await Reader.ResolveAsync(w.Slug, $"{w.Autowork}/m5/13", Ct));
        Assert.Equal(w.Autowork13, tool.Item.Id);
    }

    [Fact]
    public async Task A_project_ref_resolves_to_anything_under_the_project()
    {
        var w = await WorldAsync();

        Assert.Equal(w.Autowork13, Assert.Single(await ResolveAsync(w, $"{w.Autowork}/13")).Id);
        Assert.Equal(w.NomosMap, Assert.Single(await ResolveAsync(w, $"{w.Nomos}/repo-map")).Id);
        Assert.Empty(await ResolveAsync(w, $"{w.Nomos}/m6/repo-map"));
        Assert.Empty(await ResolveAsync(w, $"{w.Autowork}/m5/13/x"));
    }

    [Fact]
    public async Task An_ambiguous_project_ref_is_refused_with_the_longer_forms_that_each_resolve_to_one()
    {
        var w = await WorldAsync();

        Assert.Equal(2, (await ResolveAsync(w, $"{w.Autowork}/repo-map")).Count);

        var refused = Assert.IsType<ScopeResult.Ambiguous>(await Reader.ResolveAsync(w.Slug, $"{w.Autowork}/repo-map", Ct));
        Assert.Equal([$"{w.Autowork}/m5/repo-map", "m6/repo-map"], refused.Candidates.Order(StringComparer.Ordinal));

        var written = await WriteSeed.Writer(fixture).NoteAsync(
            w.WorkspaceId, WriteSeed.Orchestrator, "observation", "which one?", $"{w.Autowork}/repo-map", null, Ct);
        Assert.True(written.Refused, written.Text);
        RefusalShape.SelfCorrecting(written.Text);
        Assert.Contains($"\"{w.Autowork}/m5/repo-map\"", written.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_printed_ref_is_the_shortest_form_that_resolves_to_the_item_alone()
    {
        var w = await WorldAsync();
        var board = new BoardQueries(fixture.DataSource);

        Assert.Equal($"{w.Autowork}/13", await CallableAsync(w, w.Autowork13));
        Assert.Equal($"{w.Nomos}/13", await CallableAsync(w, w.Nomos13));
        Assert.Equal($"{w.Autowork}/m5/repo-map", await CallableAsync(w, w.AutoworkMap5));
        Assert.Equal("m6/repo-map", await CallableAsync(w, w.AutoworkMap6));
        Assert.Equal($"{w.Autowork}/m5", await CallableAsync(w, w.AutoworkM5));

        var refused = Assert.IsType<ScopeResult.Ambiguous>(await Reader.ResolveAsync(w.Slug, "13", Ct));
        Assert.Equal([$"{w.Autowork}/13", $"{w.Nomos}/13"], refused.Candidates.Order(StringComparer.Ordinal));

        async Task<string> CallableAsync(World world, Guid id)
        {
            var item = (await board.ResolveAsync(world.WorkspaceId, await RefOfAsync(id), Ct)).Single(i => i.Id == id);
            return await board.CallableRefAsync(item, refShared: true, Ct);
        }
    }

    [Fact]
    public async Task Bare_run_qualified_and_parent_qualified_forms_resolve_as_before()
    {
        var w = await WorldAsync();

        Assert.Equal(w.Lone, Assert.Single(await ResolveAsync(w, "lone")).Id);
        Assert.Equal(w.Lone, Assert.Single(await ResolveAsync(w, "m6/lone")).Id);
        Assert.Equal(w.AutoworkMap6, Assert.Single(await ResolveAsync(w, "m6/repo-map")).Id);
        Assert.Equal(w.AutoworkM5, Assert.Single(await ResolveAsync(w, $"{w.Autowork}/m5")).Id);
        Assert.Equal(2, (await ResolveAsync(w, "m5/m5")).Count);
        Assert.Equal(w.AutoworkM6, Assert.Single(await ResolveAsync(w, "m6/m6")).Id);
        Assert.Empty(await ResolveAsync(w, "/lone"));
        Assert.Empty(await ResolveAsync(w, "m6/"));
    }

    [Fact]
    public async Task A_ref_with_a_slash_is_refused_by_work_create_naming_the_field()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var result = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("followup", "A slashed ref", Parent: "run-a", Ref: "m5/13")], Ct);

        Assert.True(result.Refused, result.Text);
        RefusalShape.SelfCorrecting(result.Text);
        Assert.Contains("ref contains '/'", result.Text, StringComparison.Ordinal);
        Assert.Contains("'/' is the qualifier separator", result.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"m5-13\"", result.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.False(await db.Items.AnyAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref.Contains("/"), Ct));
    }

    [Fact]
    public async Task A_ref_with_a_slash_is_refused_by_work_import_naming_the_row()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        string project;
        await using (var db = fixture.NewContext())
        {
            project = (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
        }

        var bound = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg($"git@github.com:r-larch/slash-{world.WorkspaceId:n}.git")), Ct);
        Assert.False(bound.Refused, bound.Text);

        var result = await world.Writer.ImportAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            [new ImportItemArg(new ImportSourceArg("docs/plan.md", "## Slash"), "followup", "still open",
                Title: "A slashed ref", Ref: "m5/13", Parent: "run-a")],
            false, Ct);

        Assert.True(result.Refused, result.Text);
        Assert.Contains("items[0] (followup) ref \"m5/13\" contains '/'", result.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"m5-13\"", result.Text, StringComparison.Ordinal);
    }

    private BoardReader Reader => new(new WorkStateQueries(fixture.DataSource), new BoardQueries(fixture.DataSource));

    private Task<IReadOnlyList<ScopeRef>> ResolveAsync(World w, string reference)
        => new BoardQueries(fixture.DataSource).ResolveAsync(w.WorkspaceId, reference, Ct);

    private async Task<string> RefOfAsync(Guid id)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == id, Ct)).Ref;
    }

    private sealed record World(
        string Slug,
        Guid WorkspaceId,
        string Autowork,
        string Nomos,
        Guid AutoworkM5,
        Guid AutoworkM6,
        Guid Autowork13,
        Guid Nomos13,
        Guid AutoworkMap5,
        Guid AutoworkMap6,
        Guid NomosMap,
        Guid Lone);

    /// <summary>
    /// Two projects, each with a run <c>m5</c> holding a <c>13</c>; autowork also has <c>m6</c>.
    /// <c>repo-map</c> sits in autowork/m5, autowork/m6 and nomos/m5; <c>lone</c> only in m6.
    /// </summary>
    private async Task<World> WorldAsync()
    {
        await using var db = fixture.NewContext();

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = $"w-{Guid.CreateVersion7():n}" };
        db.Workspaces.Add(workspace);

        var autowork = Seed.NewItem(workspace.Id, null, "project", $"aw{Guid.CreateVersion7():n}"[..10], 1m);
        var nomos = Seed.NewItem(workspace.Id, null, "project", $"no{Guid.CreateVersion7():n}"[..10], 2m);
        db.Items.AddRange(autowork, nomos);
        await db.SaveChangesAsync(Ct);

        var awM5 = Seed.NewItem(workspace.Id, autowork.Id, "run", "m5", 1m);
        var awM6 = Seed.NewItem(workspace.Id, autowork.Id, "run", "m6", 2m);
        var noM5 = Seed.NewItem(workspace.Id, nomos.Id, "run", "m5", 1m);
        db.Items.AddRange(awM5, awM6, noM5);
        await db.SaveChangesAsync(Ct);

        var aw13 = Seed.NewItem(workspace.Id, awM5.Id, "followup", "13", 1m);
        var no13 = Seed.NewItem(workspace.Id, noM5.Id, "followup", "13", 1m);
        var awMap5 = Seed.NewItem(workspace.Id, awM5.Id, "followup", "repo-map", 2m);
        var awMap6 = Seed.NewItem(workspace.Id, awM6.Id, "followup", "repo-map", 1m);
        var noMap = Seed.NewItem(workspace.Id, noM5.Id, "followup", "repo-map", 2m);
        var lone = Seed.NewItem(workspace.Id, awM6.Id, "followup", "lone", 2m);
        db.Items.AddRange(aw13, no13, awMap5, awMap6, noMap, lone);
        await db.SaveChangesAsync(Ct);

        return new World(
            workspace.Slug, workspace.Id, autowork.Ref, nomos.Ref,
            awM5.Id, awM6.Id, aw13.Id, no13.Id, awMap5.Id, awMap6.Id, noMap.Id, lone.Id);
    }
}

using Microsoft.EntityFrameworkCore;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.AgentImport;

/// <summary>
/// import-thread-refs. The first real import carried kebab refs on its assumptions; they were
/// dropped without a word and A1/A2 assigned. A ref is now honoured where the kind has one and
/// refused where it has none — never dropped.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ImportRefTests(PostgresFixture fixture)
{
    private static ImportSourceArg At(string anchor) => new("docs/plan.md", anchor);

    [Theory]
    [InlineData("note", "notes have no ref; remove it")]
    [InlineData("rule", "a rule's ref is numbered C<n> by the server; remove it")]
    public async Task A_ref_on_a_kind_that_has_none_is_refused_naming_the_field_and_nothing_is_written(string kind, string fix)
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world, [new(At("## One"), kind, "still true", Body: "The text.", Ref: "kept-ref")]);

        Assert.True(result.Refused, result.Text);
        Assert.Contains($"items[0] ({kind}) carries ref \"kept-ref\" — {fix}", result.Text, StringComparison.Ordinal);
        Assert.Contains("with items[0] without ref", result.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.False(await db.ImportRecords.AnyAsync(r => r.WorkspaceId == world.WorkspaceId, Ct));
    }

    [Fact]
    public async Task An_assumption_keeps_the_ref_it_was_imported_with_and_work_get_reads_it_back()
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world,
        [
            new(At("## Reach"), "assumption", "the probe still answers", Body: "The dev machine reaches the export endpoint.", Ref: "export-reachable"),
            new(At("## Other"), "assumption", "nothing contradicts it", Body: "One project per repo holds."),
        ]);

        Assert.False(result.Refused, result.Text);
        Assert.Contains(" export-reachable ", result.Text, StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            var refs = await db.Assumptions.Where(a => a.WorkspaceId == world.WorkspaceId).Select(a => a.Ref).OrderBy(r => r).ToListAsync(Ct);
            Assert.Equal(["A1", "export-reachable"], refs);
        }

        var got = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["export-reachable"], [], Ct);
        Assert.Contains("  statement The dev machine reaches the export endpoint.", got, StringComparison.Ordinal);
        Assert.Contains("  imported  from docs/plan.md### Reach by orchestrator-a, just now", got, StringComparison.Ordinal);
        Assert.Contains("  why       the probe still answers", got, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_assumption_ref_that_is_taken_in_its_run_is_refused_not_renumbered()
    {
        var world = await BoundWorldAsync();
        Assert.False((await ImportAsync(world, [new(At("## First"), "assumption", "why", Body: "First.")])).Refused);

        var result = await ImportAsync(world, [new(At("## Second"), "assumption", "why", Body: "Second.", Ref: "A1")]);

        Assert.True(result.Refused, result.Text);
        Assert.Contains("ref \"A1\" is taken under", result.Text, StringComparison.Ordinal);
        Assert.Contains("without ref — the server numbers it A<n>", result.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Equal(1, await db.Assumptions.CountAsync(a => a.WorkspaceId == world.WorkspaceId, Ct));
    }

    [Fact]
    public async Task A_re_import_never_renames_an_assumption_and_says_so()
    {
        var world = await BoundWorldAsync();
        Assert.False((await ImportAsync(world, [new(At("## Reach"), "assumption", "why", Body: "Reachable.", Ref: "reach")])).Refused);

        var result = await ImportAsync(world, [new(At("## Reach"), "assumption", "why", Body: "Still reachable.", Ref: "reach-2")]);

        Assert.True(result.Refused, result.Text);
        Assert.Contains("ref \"reach-2\", but this source was imported as reach", result.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var only = await db.Assumptions.SingleAsync(a => a.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal(("reach", "Reachable."), (only.Ref, only.Statement));
    }

    [Fact]
    public async Task An_assumption_ref_with_a_slash_is_refused_before_anything_is_written()
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world, [new(At("## Reach"), "assumption", "why", Body: "Reachable.", Ref: "run-a/reach")]);

        Assert.True(result.Refused, result.Text);
        Assert.Contains("items[0] (assumption) ref \"run-a/reach\" is not one word", result.Text, StringComparison.Ordinal);
    }

    private async Task<WriteWorld> BoundWorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var bound = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            await ProjectRefAsync(world),
            new UpdateArgs(Repo: new RepoArg($"git@github.com:r-larch/import-{world.WorkspaceId:n}.git")),
            Ct);
        Assert.False(bound.Refused, bound.Text);
        return world;
    }

    private async Task<WriteResult> ImportAsync(WriteWorld world, ImportItemArg[] items)
        => await world.Writer.ImportAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, await ProjectRefAsync(world), items, false, Ct);

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }
}

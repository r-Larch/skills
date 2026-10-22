using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// ws/13. With a run <c>backlog</c> and a phase <c>m0/backlog</c> both present, the refusal for the
/// bare ref offered <c>ref:"backlog"</c> — the identical ambiguous string — because a run's qualified
/// ref is its bare ref. Every suggestion an ambiguous-ref refusal prints must resolve to exactly one
/// of the matches, on every surface.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AmbiguousRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_run_and_a_phase_sharing_a_ref_are_refused_with_retries_that_each_resolve_to_one()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world, "backlog");
        var phase = await WriteSeed.ChildAsync(world, world.RunId, "backlog", kind: "phase");
        var run = (await RunAsync(world)).Id;
        Guid[] both = [run, phase];

        var moved = await world.Writer.MoveAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "backlog", "run-a", null, null, Ct);
        Assert.True(moved.Refused, moved.Text);
        RefusalShape.SelfCorrecting(moved.Text);
        await AssertEachResolvesToOneAsync(world, Quoted(moved.Text, @"retry with ref:""([^""]+)"""), both);

        var scope = Assert.IsType<ScopeResult.Ambiguous>(await world.Reader.ResolveAsync(world.Slug, "backlog", Ct));
        await AssertEachResolvesToOneAsync(world, scope.Candidates, both);

        var explained = ScopeExplain.Text(scope, "work_brief", "backlog");
        await AssertEachResolvesToOneAsync(world, Quoted(explained, @"work_brief\(scope:""([^""]+)""\)"), both);

        var got = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["backlog"], [], Ct);
        var listed = Regex.Match(got, @"work_get\(refs:\[([^\]]*)\]\)");
        Assert.True(listed.Success, got);
        await AssertEachResolvesToOneAsync(world, Quoted(listed.Groups[1].Value, @"""([^""]+)"""), both);
    }

    /// <summary>
    /// The assumption twin (ws/7): a workspace-level A1 has no qualified form at all, so printing its
    /// bare ref back is the same trap. Only forms that single one out may be offered.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_assumption_ref_is_refused_only_with_forms_that_each_resolve_to_one()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var opened = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", "an assumption in run-a", "reasoned", "medium",
            null, null, null, Ct);
        Assert.False(opened.Refused, opened.Text);

        await using (var db = fixture.NewContext())
        {
            db.Assumptions.Add(new Assumption
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                Ref = "A1",
                Statement = "an assumption about the whole workspace",
                Evidence = EvidenceLevel.Reasoned,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var refused = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "A1", null, "settled", null, null, "confirmed", null, Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);

        var suggested = Quoted(refused.Text, @"→\s+work_assume\(ref:""([^""]+)""");
        Assert.NotEmpty(suggested);

        await using var check = fixture.NewContext();
        foreach (var form in suggested)
        {
            var slash = form.LastIndexOf('/');
            var leaf = slash > 0 ? form[(slash + 1)..] : form;
            var qualifier = slash > 0 ? form[..slash] : null;

            var count = await check.Assumptions.CountAsync(
                a => a.WorkspaceId == world.WorkspaceId
                     && a.State == AssumptionState.Open
                     && a.Ref == leaf
                     && (qualifier == null
                         || check.Items.Any(i => i.Id == a.RefScopeId && i.Ref == qualifier)),
                Ct);

            Assert.True(count == 1, $"\"{form}\" resolves to {count} open assumptions:\n{refused.Text}");
        }
    }

    private async Task AssertEachResolvesToOneAsync(WriteWorld world, IReadOnlyList<string> forms, Guid[] matches)
    {
        Assert.NotEmpty(forms);

        var board = new BoardQueries(fixture.DataSource);
        var reached = new HashSet<Guid>();

        foreach (var form in forms)
        {
            var found = await board.ResolveAsync(world.WorkspaceId, form, Ct);
            Assert.True(found.Count == 1, $"\"{form}\" resolves to {found.Count} items");
            Assert.Contains(found[0].Id, matches);
            reached.Add(found[0].Id);
        }

        Assert.Equal(matches.ToHashSet(), reached);
    }

    private static IReadOnlyList<string> Quoted(string text, string pattern)
        => [.. Regex.Matches(text, pattern).Select(m => m.Groups[1].Value)];

    private async Task<Item> RunAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return await db.Items.SingleAsync(
            i => i.WorkspaceId == world.WorkspaceId && i.Kind == "run" && i.Ref == "backlog", Ct);
    }
}

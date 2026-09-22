using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// U9.1. <c>constraint_rule</c> had exactly one writer — the importer — so the BINDING block that
/// opens every brief was import-only: a project started in the store got an empty one forever. And
/// the table had no <c>ref</c>, so a rule could be read but never cited, questioned or retired.
/// These assert both halves through the brief, because the brief is where a rule is actually read.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ConstrainTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_rule_written_through_the_tool_appears_in_the_next_brief_with_its_ref()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var wrote = await world.Writer.ConstrainAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            null,
            new ConstrainArgs(
                Kind: "red_line",
                Text: "never connect to gaia.factory.it",
                Route: "use the local compose database on 55433"),
            Ct);

        Assert.False(wrote.Refused, wrote.Text);
        Assert.Contains("C1", wrote.Text, StringComparison.Ordinal);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "resume", Ct);

        Assert.Contains("BINDING (1)", brief, StringComparison.Ordinal);

        var line = brief.Split('\n').Single(l => l.Contains("gaia.factory.it", StringComparison.Ordinal));
        Assert.Equal(
            "  ! C1  never connect to gaia.factory.it — use the local compose database on 55433",
            line);
    }

    [Fact]
    public async Task Retiring_by_ref_removes_it_from_the_brief_and_leaves_the_row_inactive()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await world.Writer.ConstrainAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            null,
            new ConstrainArgs(Kind: "command", Text: "run the suite with --filter"),
            Ct);

        var retired = await world.Writer.ConstrainAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "C1",
            new ConstrainArgs(Retire: true, Reason: "the suite is isolated now, the filter is noise"),
            Ct);

        Assert.False(retired.Refused, retired.Text);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "resume", Ct);
        Assert.DoesNotContain("BINDING", brief, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var row = await db.ConstraintRules.SingleAsync(c => c.WorkspaceId == world.WorkspaceId, Ct);

        Assert.Equal("C1", row.Ref);
        Assert.False(row.Active);
        Assert.Equal("run the suite with --filter", row.Text);
    }

    /// <summary>
    /// The plausible wrong implementation: minting per call rather than per scope, so two rules
    /// collide on one ref — or the same rule is renamed between two briefs.
    /// </summary>
    [Fact]
    public async Task Two_rules_in_one_workspace_get_different_refs_and_neither_moves_between_reads()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        foreach (var text in new[] { "the first rule", "the second rule" })
        {
            var wrote = await world.Writer.ConstrainAsync(
                world.WorkspaceId, WriteSeed.Orchestrator, null,
                new ConstrainArgs(Kind: "domain_rule", Text: text), Ct);

            Assert.False(wrote.Refused, wrote.Text);
        }

        await using var db = fixture.NewContext();
        var refs = await db.ConstraintRules
            .Where(c => c.WorkspaceId == world.WorkspaceId)
            .OrderBy(c => c.Ref)
            .Select(c => new { c.Ref, c.Text })
            .ToListAsync(Ct);

        Assert.Equal(new[] { "C1", "C2" }, refs.Select(r => r.Ref).ToArray());

        var scope = await world.ScopeAsync("1.1");
        var first = await world.Reader.BriefAsync(scope, "resume", Ct);
        var second = await world.Reader.BriefAsync(scope, "resume", Ct);

        foreach (var row in refs)
        {
            var expected = $"· {row.Ref}";
            Assert.Contains(expected, first, StringComparison.Ordinal);
            Assert.Contains(expected, second, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_rule_scoped_to_an_item_binds_that_subtree_and_a_workspace_rule_binds_both()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);

        await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, null,
            new ConstrainArgs(Kind: "domain_rule", Text: "everything here is English", Scope: "1.1"), Ct);

        await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, null,
            new ConstrainArgs(Kind: "stack_fact", Text: "postgres 17 on 55433"), Ct);

        var here = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "resume", Ct);
        var sibling = await world.Reader.BriefAsync(await world.ScopeAsync("9.1"), "resume", Ct);

        Assert.Contains("BINDING (2)", here, StringComparison.Ordinal);
        Assert.Contains("everything here is English", here, StringComparison.Ordinal);
        Assert.Contains("postgres 17 on 55433", here, StringComparison.Ordinal);

        Assert.Contains("BINDING (1)", sibling, StringComparison.Ordinal);
        Assert.DoesNotContain("everything here is English", sibling, StringComparison.Ordinal);
        Assert.Contains("postgres 17 on 55433", sibling, StringComparison.Ordinal);
    }

    /// <summary>
    /// The route is required for a red line and for nothing else. doc 07 §3's lesson is that a guard
    /// nobody can satisfy honestly teaches everyone the override — this one is satisfiable in every
    /// case, because "stop and report it" is a route, and there is no override to teach.
    /// </summary>
    [Fact]
    public async Task A_red_line_without_a_route_is_refused_and_a_domain_rule_without_one_is_not()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, null,
            new ConstrainArgs(Kind: "red_line", Text: "production is read-only until phase 9"), Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("route:", refused.Text, StringComparison.Ordinal);

        var allowed = await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, null,
            new ConstrainArgs(Kind: "domain_rule", Text: "ids are uuidv7"), Ct);

        Assert.False(allowed.Refused, allowed.Text);

        await using var db = fixture.NewContext();
        var rules = await db.ConstraintRules.Where(c => c.WorkspaceId == world.WorkspaceId).ToListAsync(Ct);

        Assert.Equal("ids are uuidv7", Assert.Single(rules).Text);
    }

    [Fact]
    public async Task Retiring_without_a_reason_or_without_a_ref_is_refused_with_the_call_that_fixes_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, null,
            new ConstrainArgs(Kind: "command", Text: "always pass --connection"), Ct);

        var unnamed = await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, null, new ConstrainArgs(Retire: true), Ct);

        var unreasoned = await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "C1", new ConstrainArgs(Retire: true), Ct);

        var unknown = await world.Writer.ConstrainAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "C9",
            new ConstrainArgs(Retire: true, Reason: "stale"), Ct);

        foreach (var result in new[] { unnamed, unreasoned, unknown })
        {
            Assert.True(result.Refused, result.Text);
            RefusalShape.SelfCorrecting(result.Text);
        }

        await using var db = fixture.NewContext();
        var row = await db.ConstraintRules.SingleAsync(c => c.WorkspaceId == world.WorkspaceId, Ct);

        Assert.True(row.Active, "a refused retirement left the rule inactive");
    }
}

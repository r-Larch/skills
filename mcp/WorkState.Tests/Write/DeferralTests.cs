using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 07 §4. There is no <c>deferred</c> status: the corpus has 151 mentions of "Deferred" behind
/// a follow-up list nobody ever closed, so deferring has to produce something a later
/// <c>work_next</c> actually returns. A deferral that produces an item nobody will ever see is the
/// original failure wearing a schema.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class DeferralTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Deferring_to_nowhere_is_refused_and_the_refusal_names_where_it_could_go()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "out of scope"), Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("target:\"backlog\"", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
    }

    [Fact]
    public async Task Deferring_with_a_target_creates_a_real_open_item_that_work_next_returns()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "the carried criterion", ticked: false);

        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "belongs in the next run", Target: "backlog"), Ct);

        Assert.False(moved.Refused, moved.Text);
        Assert.Contains("1.1-d", moved.Text, StringComparison.Ordinal);

        var original = await world.ItemAsync("1.1");
        Assert.Equal(ItemStatus.Done, original.Status);
        Assert.Equal(ItemResolution.Obsolete, original.Resolution);

        var carried = await world.ItemAsync("1.1-d");
        Assert.Equal(ItemStatus.Open, carried.Status);
        Assert.Equal("followup", carried.Kind);

        await using var db = fixture.NewContext();

        Assert.True(await db.ItemLinks.AnyAsync(
            l => l.FromItemId == carried.Id && l.ToItemId == original.Id && l.Type == LinkType.DerivedFrom, Ct));

        Assert.Equal(1, await db.ItemCriteria.CountAsync(c => c.ItemId == carried.Id, Ct));

        // ws/12: "backlog" is a project-level run, so the follow-up is on that run's frontier — the
        // one the OK line names — and no longer under run-a.
        var named = Regex.Match(moved.Text, @"work_next\(scope:""([^""]+)""\)");
        Assert.True(named.Success, moved.Text);

        var frontier = await world.Reader.NextAsync(await world.ScopeAsync(named.Groups[1].Value), 20, null, Ct);
        Assert.Contains("1.1-d", frontier, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deferring_to_an_existing_ref_puts_the_work_there_rather_than_in_a_backlog()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "phase 2's problem", Target: "2"), Ct);

        Assert.False(moved.Refused, moved.Text);

        var carried = await world.ItemAsync("1.1-d");
        var phase = await world.ItemAsync("2");

        Assert.Equal(phase.Id, carried.ParentId);
    }

    [Fact]
    public async Task A_deferral_is_recorded_as_a_deferral_not_as_a_quiet_close()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "not now", Target: "backlog"), Ct);

        Assert.Contains("deferred", await WriteSeed.EventKindsAsync(world, world.TaskId));
    }
}

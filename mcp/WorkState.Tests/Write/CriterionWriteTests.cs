using Microsoft.EntityFrameworkCore;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 01 §3.2's criteria contract: an id addresses a criterion that exists, a text with no id
/// appends. Observed live: <c>criteria:[{id:"c4", text:"…"}]</c> on an item with c1–c3 answered
/// <c>OK … updated</c> and wrote nothing. An OK over a write that did not happen is a silent drop.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class CriterionWriteTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_id_that_does_not_exist_is_refused_naming_the_ids_that_do_and_the_append_form()
    {
        var world = await ThreeCriteriaAsync();
        var before = (await world.ItemAsync("1.1")).Revision;

        var refused = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "1.1",
            new UpdateArgs(Title: "renamed", Criteria: [new CriterionArg("c4", Text: "the fourth")]),
            Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("c1, c2, c3", refused.Text, StringComparison.Ordinal);
        Assert.Contains("criteria:[{text:\"the fourth\"}]", refused.Text, StringComparison.Ordinal);

        // Refused as a whole: neither the criterion nor the field beside it was written.
        var item = await world.ItemAsync("1.1");
        Assert.Equal(before, item.Revision);
        Assert.NotEqual("renamed", item.Title);
        Assert.Equal(3, await CountAsync(world));
    }

    [Fact]
    public async Task The_append_form_the_refusal_names_writes_the_criterion_and_counts_it()
    {
        var world = await ThreeCriteriaAsync();

        var written = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "1.1",
            new UpdateArgs(Criteria: [new CriterionArg(Text: "the fourth")]),
            Ct);

        Assert.False(written.Refused, written.Text);
        Assert.Contains("1 criterion(s) written", written.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var fourth = await db.ItemCriteria.SingleAsync(c => c.ItemId == world.TaskId && c.Seq == 4, Ct);
        Assert.Equal("the fourth", fourth.Text);
    }

    [Fact]
    public async Task The_result_counts_exactly_the_criteria_it_wrote()
    {
        var world = await ThreeCriteriaAsync();

        var written = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "1.1",
            new UpdateArgs(Criteria:
            [
                new CriterionArg("c2", Checked: true, Evidence: "the assertion"),
                new CriterionArg(Text: "the fourth"),
            ]),
            Ct);

        Assert.False(written.Refused, written.Text);
        Assert.Contains("2 criterion(s) written", written.Text, StringComparison.Ordinal);
        Assert.Equal(4, await CountAsync(world));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("c2", null)]
    [InlineData("second", "the second, renamed")]
    public async Task A_row_that_would_write_nothing_is_refused_rather_than_skipped(string? id, string? text)
    {
        var world = await ThreeCriteriaAsync();

        var refused = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "1.1",
            new UpdateArgs(Criteria: [new CriterionArg(id, Text: text)]),
            Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Equal(3, await CountAsync(world));
    }

    private async Task<WriteWorld> ThreeCriteriaAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "the first", ticked: false);
        await WriteSeed.CriterionAsync(world, world.TaskId, 2, "the second", ticked: false);
        await WriteSeed.CriterionAsync(world, world.TaskId, 3, "the third", ticked: false);
        return world;
    }

    private async Task<int> CountAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return await db.ItemCriteria.CountAsync(c => c.ItemId == world.TaskId, Ct);
    }
}

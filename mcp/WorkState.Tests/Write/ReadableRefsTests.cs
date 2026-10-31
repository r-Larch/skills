using Microsoft.EntityFrameworkCore;
using WorkState.Core.Writing;
using WorkState.Mcp;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// m66. Under a project or its backlog a human picks work by name, so <c>1</c> or <c>3-d</c> there says
/// nothing: work_create refuses an unnamed item and suggests a slug, and a deferral lands on a name.
/// Inside any other run the number is the plan's order and stays.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ReadableRefsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReadableRefs_an_unnamed_item_under_the_backlog_is_refused_with_a_slug_and_the_slug_then_succeeds()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world, "backlog");

        var refused = await CreateAsync(world, new NewItemArg("followup", "Cap the voice buffer!", Parent: "backlog"));

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("Nothing was written", refused.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"cap-voice-buffer\"", refused.Text, StringComparison.Ordinal);
        await AssertAbsentAsync(world, "Cap the voice buffer!");

        var created = await CreateAsync(
            world, new NewItemArg("followup", "Cap the voice buffer!", Ref: "cap-voice-buffer", Parent: "backlog"));

        Assert.False(created.Refused, created.Text);
        Assert.Equal("Cap the voice buffer!", (await world.ItemAsync("cap-voice-buffer")).Title);
    }

    [Fact]
    public async Task ReadableRefs_an_unnamed_run_under_a_project_is_refused_with_every_unnamed_item_and_nothing_written()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        var refused = await CreateAsync(
            world,
            new NewItemArg("run", "Entity store", Parent: project),
            new NewItemArg("phase", "Named under a numbered run", Parent: "run-a"),
            new NewItemArg("followup", "The entity store", Parent: project));

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("2 item(s)", refused.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"entity-store\"", refused.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"entity-store-2\"", refused.Text, StringComparison.Ordinal);
        await AssertAbsentAsync(world, "Entity store");
        await AssertAbsentAsync(world, "Named under a numbered run");

        var created = await CreateAsync(world, new NewItemArg("run", "Entity store", Ref: "entity-store", Parent: project));

        Assert.False(created.Refused, created.Text);
        Assert.Equal("run", (await world.ItemAsync("entity-store")).Kind);
    }

    [Fact]
    public async Task ReadableRefs_inside_an_ordinary_run_an_unnamed_item_is_still_numbered()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await CreateAsync(
            world,
            new NewItemArg("phase", "Third phase", Parent: "run-a"),
            new NewItemArg("phase", "Fourth phase", Parent: "run-a"));

        Assert.False(created.Refused, created.Text);
        Assert.Equal("Third phase", (await world.ItemAsync("3")).Title);
        Assert.Equal("Fourth phase", (await world.ItemAsync("4")).Title);
    }

    [Theory]
    [InlineData("Cap the voice buffer!", "cap-voice-buffer")]
    [InlineData("OpenAI TTS: vendor (d)", "openai-tts-vendor-d")]
    [InlineData("Größe der Übersicht prüfen", "groesse-der-uebersicht-pruefen")]
    [InlineData("Café crème façade", "cafe-creme-facade")]
    [InlineData("日本語 only", "only")]
    [InlineData("日本語", "followup")]
    [InlineData("Refactor the extraordinarily-complicated serialization pipeline today", "refactor-extraordinarily")]
    [InlineData("Supercalifragilisticexpialidocious-and-then-some-more", "supercalifragilisticexpialidocio")]
    [InlineData("The and of it", "the-and-of-it")]
    [InlineData("2026", "followup-2026")]
    [InlineData("  --  ", "followup")]
    public void ReadableRefs_slug_of_a_title(string title, string slug)
    {
        var made = RefSlug.From(title, "followup");

        Assert.Equal(slug, made);
        Assert.True(made.Length <= RefSlug.MaxLength, made);
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", made);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("2.3", true)]
    [InlineData("F-07", true)]
    [InlineData("3-d", true)]
    [InlineData("1.1-d2", true)]
    [InlineData("voice-buffer-cap", false)]
    [InlineData("3.R1", false)]
    [InlineData("m66", false)]
    public void ReadableRefs_a_numbered_ref_is_told_from_a_name(string reference, bool numbered)
        => Assert.Equal(numbered, RefSlug.IsNumbered(reference));

    [Fact]
    public async Task ReadableRefs_a_slug_taken_by_a_sibling_is_suggested_with_a_free_variant()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world, "backlog");
        await WriteSeed.ChildAsync(world, (await world.ItemAsync("backlog")).Id, "cap-voice-buffer", kind: "followup");

        var refused = await CreateAsync(world, new NewItemArg("followup", "Cap the voice buffer", Parent: "backlog"));

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains("ref:\"cap-voice-buffer-2\"", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadableRefs_deferring_a_named_item_to_the_backlog_keeps_its_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var named = await WriteSeed.ChildAsync(world, world.PhaseId, "voice-cap");

        var moved = await DeferAsync(world, "voice-cap");

        Assert.False(moved.Refused, moved.Text);
        var carried = await CarriedAsync(world, named);
        Assert.Equal("voice-cap", carried.Ref);
        Assert.Equal("backlog", await ParentRefAsync(carried.ParentId));
    }

    [Fact]
    public async Task ReadableRefs_deferring_a_numbered_item_to_the_backlog_takes_its_title_slug()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var numbered = await WriteSeed.ChildAsync(world, world.PhaseId, "3");

        var moved = await DeferAsync(world, "3");

        Assert.False(moved.Refused, moved.Text);
        Assert.Equal("child-3", (await CarriedAsync(world, numbered)).Ref);
        Assert.DoesNotContain("3-d", moved.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadableRefs_deferring_a_name_taken_in_the_backlog_takes_a_free_variant()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world, "backlog");
        await WriteSeed.ChildAsync(world, (await world.ItemAsync("backlog")).Id, "child-voice-cap", kind: "followup");
        await WriteSeed.ChildAsync(world, (await world.ItemAsync("backlog")).Id, "voice-cap", kind: "followup");
        var named = await WriteSeed.ChildAsync(world, world.PhaseId, "voice-cap");

        var moved = await DeferAsync(world, "run-a/voice-cap");

        Assert.False(moved.Refused, moved.Text);
        Assert.Equal("voice-cap-2", (await CarriedAsync(world, named)).Ref);
    }

    [Fact]
    public void ReadableRefs_the_instructions_name_the_link_form_inside_their_budget()
    {
        var lines = WorkStateServerInstructions.Text.Split('\n');

        Assert.True(lines.Length <= WorkStateServerInstructions.LineBudget, $"ServerInstructions is {lines.Length} lines");
        Assert.Contains("[short-ref](<base>/r/<full ref>)", WorkStateServerInstructions.Text, StringComparison.Ordinal);
        Assert.Contains("never URLs", WorkStateServerInstructions.Text, StringComparison.Ordinal);
    }

    private static Task<WriteResult> CreateAsync(WriteWorld world, params NewItemArg[] items)
        => world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator, items, Ct);

    private static Task<WriteResult> DeferAsync(WriteWorld world, string reference)
        => world.Writer.StatusAsync(
            world.WorkspaceId, reference, "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "not this run", Target: "backlog"), Ct);

    private async Task<Core.Domain.Item> CarriedAsync(WriteWorld world, Guid deferred)
    {
        await using var db = fixture.NewContext();
        var link = await db.ItemLinks.SingleAsync(l => l.ToItemId == deferred && l.Type == Core.Domain.LinkType.DerivedFrom, Ct);
        return await db.Items.SingleAsync(i => i.Id == link.FromItemId && i.WorkspaceId == world.WorkspaceId, Ct);
    }

    private async Task<string> ParentRefAsync(Guid? parentId)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == parentId, Ct)).Ref;
    }

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private async Task AssertAbsentAsync(WriteWorld world, string title)
    {
        await using var db = fixture.NewContext();
        Assert.False(await db.Items.AnyAsync(i => i.WorkspaceId == world.WorkspaceId && i.Title == title, Ct));
    }
}

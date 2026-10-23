using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// F-08. Only the addressee's worker brief consumes a note, and nobody briefs a closed item, so a
/// note addressed to one repeated in attention on every call — "1 note(s) addressed to 8-d
/// unconsumed" long after 8-d was superseded. A closed addressee leaves attention; a superseded one
/// hands its notes to the item that replaced it, because they are still somebody's obligation.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClosedAddresseeTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(ItemStatus.Done)]
    [InlineData(ItemStatus.Cancelled)]
    [InlineData(ItemStatus.Superseded)]
    public async Task A_note_addressed_to_a_closed_item_raises_no_attention_line(ItemStatus closed)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await NoteAsync(world, world.TaskId, "the facet cap applies here too");
        await WriteSeed.StatusAsync(world, world.TaskId, closed);

        var lines = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);

        Assert.DoesNotContain(lines, l => l.Rank == AttentionRank.UnconsumedNote);
    }

    [Fact]
    public async Task A_note_addressed_to_a_closed_item_stays_readable_in_work_threads()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await NoteAsync(world, world.TaskId, "the facet cap applies here too");
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);

        var threads = await world.Reader.ThreadsAsync(await world.ScopeAsync("run-a"), ["notes"], Ct);

        Assert.Contains("the facet cap applies here too", threads, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_addressed_to_an_open_item_still_raises_its_attention_line()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await NoteAsync(world, world.TaskId, "the facet cap applies here too");

        var lines = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);

        var note = Assert.Single(lines, l => l.Rank == AttentionRank.UnconsumedNote);
        Assert.Equal("1 note(s) addressed to 1.1 unconsumed", note.What);
    }

    [Fact]
    public async Task Superseding_an_item_carries_its_unconsumed_notes_to_the_replacement()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var replacement = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        var pending = await NoteAsync(world, world.TaskId, "the facet cap applies here too");
        var consumed = await NoteAsync(world, world.TaskId, "already read", consumedAt: DateTimeOffset.UtcNow);

        var linked = await world.Writer.LinkAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.2", "1.1", "supersedes", false, Ct);

        Assert.False(linked.Refused, linked.Text);

        await using var db = fixture.NewContext();
        Assert.Equal(replacement, (await db.Notes.SingleAsync(n => n.Id == pending, Ct)).ToItemId);
        Assert.Equal(world.TaskId, (await db.Notes.SingleAsync(n => n.Id == consumed, Ct)).ToItemId);

        var lines = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);
        var note = Assert.Single(lines, l => l.Rank == AttentionRank.UnconsumedNote);
        Assert.Equal("1 note(s) addressed to 1.2 unconsumed", note.What);
    }

    private async Task<Guid> NoteAsync(
        WriteWorld world,
        Guid toItemId,
        string body,
        DateTimeOffset? consumedAt = null)
    {
        await using var db = fixture.NewContext();
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            FromItemId = world.RunId,
            ToItemId = toItemId,
            Kind = NoteKind.CarryForward,
            Body = body,
            CreatedBy = "test",
            ConsumedAt = consumedAt,
        };

        db.Notes.Add(note);
        await db.SaveChangesAsync(Ct);
        return note.Id;
    }
}

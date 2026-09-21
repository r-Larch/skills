using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// FINDINGS-live F-L3. The 140 notes an imported run carries all share one <c>created_at</c>, so an
/// ordering that names only that column leaves every row tied and the first line of the block is
/// whatever the scan happened to return. This is the shape that produced it, in miniature.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class NoteRankingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Notes_written_in_one_batch_rank_by_cost_of_ignoring_not_by_insertion_order()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using (var db = fixture.NewContext())
        {
            // One SaveChanges, so every row takes the same transaction timestamp — exactly what the
            // importer does, and exactly what made the old ordering a coin toss.
            foreach (var kind in (NoteKind[])
                     [NoteKind.CarryForward, NoteKind.Decision, NoteKind.Incident, NoteKind.Correction])
            {
                db.Notes.Add(new Note
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = world.WorkspaceId,
                    FromItemId = world.RunA,
                    Kind = kind,
                    Body = $"note of kind {kind}",
                    CreatedBy = "test",
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        var notes = await new BoardQueries(fixture.DataSource)
            .UnconsumedNotesAsync(world.WorkspaceId, world.RunA, Ct);

        Assert.Equal(
            [NoteKind.Correction, NoteKind.Incident, NoteKind.CarryForward, NoteKind.Decision],
            notes.Select(n => n.Kind));
    }

    [Fact]
    public async Task A_note_addressed_to_an_item_still_outranks_every_broadcast_note()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using (var db = fixture.NewContext())
        {
            var task = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "t1", 1m, "First task");
            db.Items.Add(task);
            await db.SaveChangesAsync(Ct);

            db.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                FromItemId = world.RunA,
                Kind = NoteKind.Correction,
                Body = "a correction addressed to nobody",
                CreatedBy = "test",
            });

            db.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                FromItemId = world.RunA,
                ToItemId = task.Id,
                Kind = NoteKind.Environment,
                Body = "truncate facet values to 512 before insert",
                CreatedBy = "test",
            });

            await db.SaveChangesAsync(Ct);
        }

        var notes = await new BoardQueries(fixture.DataSource)
            .UnconsumedNotesAsync(world.WorkspaceId, world.RunA, Ct);

        Assert.Equal("t1", notes[0].ToRef);
    }
}

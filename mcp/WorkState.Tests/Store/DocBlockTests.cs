using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

[Collection(StoreCollection.Name)]
public sealed class DocBlockTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Editing_a_block_bumps_the_revision_and_files_the_previous_text()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var doc = new Doc
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ItemId = world.RunA,
            Slug = "brief",
            Title = "Brief",
        };
        var block = new DocBlock
        {
            Id = Guid.CreateVersion7(),
            DocId = doc.Id,
            Seq = 1m,
            ShortId = "b1",
            Heading = "Scope",
            Body = "the brief as issued",
        };
        context.Docs.Add(doc);
        context.DocBlocks.Add(block);
        await context.SaveChangesAsync(Ct);

        block.Body = "the brief as it stands now";
        await context.SaveChangesAsync(Ct);

        var current = await context.DocBlocks.AsNoTracking().SingleAsync(b => b.Id == block.Id, Ct);
        Assert.Equal(2, current.Revision);
        Assert.Equal("the brief as it stands now", current.Body);

        var history = await context.DocBlockRevisions.AsNoTracking()
            .Where(r => r.BlockId == block.Id)
            .ToListAsync(Ct);

        var previous = Assert.Single(history);
        Assert.Equal(1, previous.Revision);
        Assert.Equal("the brief as issued", previous.Body);
        Assert.Equal("Scope", previous.Heading);
    }

    [Fact]
    public async Task An_update_that_changes_no_text_does_not_create_a_revision()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var doc = new Doc
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ItemId = world.RunB,
            Slug = "handoff",
            Title = "Handoff",
        };
        var block = new DocBlock
        {
            Id = Guid.CreateVersion7(),
            DocId = doc.Id,
            Seq = 1m,
            ShortId = "b1",
            Body = "unchanged",
        };
        context.Docs.Add(doc);
        context.DocBlocks.Add(block);
        await context.SaveChangesAsync(Ct);

        block.HeadingLevel = 2;
        await context.SaveChangesAsync(Ct);

        var current = await context.DocBlocks.AsNoTracking().SingleAsync(b => b.Id == block.Id, Ct);
        Assert.Equal(1, current.Revision);
        Assert.Empty(await context.DocBlockRevisions.AsNoTracking()
            .Where(r => r.BlockId == block.Id)
            .ToListAsync(Ct));
    }
}

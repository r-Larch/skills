using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// The schema's value is what it refuses. Every test here asserts a rejection, or asserts that the
/// near-miss case is still accepted — a constraint that refuses everything is as wrong as no
/// constraint at all.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TreeConstraintTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Duplicate_ref_within_one_run_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using (var context = fixture.NewContext())
        {
            context.Items.Add(Seed.NewItem(world.WorkspaceId, world.RunA, "task", "2.3", 1m));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Items.Add(Seed.NewItem(world.WorkspaceId, world.RunA, "task", "2.3", 2m));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("item_ref_unique", error.ConstraintName);
    }

    [Fact]
    public async Task The_same_ref_under_a_different_run_is_accepted()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var inRunA = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "2.3", 1m);
        var inRunB = Seed.NewItem(world.WorkspaceId, world.RunB, "task", "2.3", 1m);
        context.Items.AddRange(inRunA, inRunB);

        await context.SaveChangesAsync(Ct);

        Assert.Equal(world.RunA, inRunA.RefScopeId);
        Assert.Equal(world.RunB, inRunB.RefScopeId);
    }

    [Fact]
    public async Task A_blocks_cycle_is_refused_and_the_error_names_the_path()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var first = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "c1", 1m);
        var second = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "c2", 2m);
        var third = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "c3", 3m);

        await using (var context = fixture.NewContext())
        {
            context.Items.AddRange(first, second, third);
            await context.SaveChangesAsync(Ct);

            context.ItemLinks.AddRange(
                NewLink(world.WorkspaceId, first.Id, second.Id, LinkType.Blocks),
                NewLink(world.WorkspaceId, second.Id, third.Id, LinkType.Blocks));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.ItemLinks.Add(NewLink(world.WorkspaceId, third.Id, first.Id, LinkType.Blocks));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Contains("blocks cycle refused", error.MessageText);
        Assert.Contains("c3 -> c1 -> c2 -> c3", error.MessageText);
    }

    [Fact]
    public async Task A_blocks_chain_that_does_not_close_is_accepted()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var first = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "d1", 1m);
        var second = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "d2", 2m);
        var third = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "d3", 3m);
        context.Items.AddRange(first, second, third);
        await context.SaveChangesAsync(Ct);

        context.ItemLinks.AddRange(
            NewLink(world.WorkspaceId, first.Id, second.Id, LinkType.Blocks),
            NewLink(world.WorkspaceId, second.Id, third.Id, LinkType.Blocks),
            NewLink(world.WorkspaceId, first.Id, third.Id, LinkType.Blocks));
        await context.SaveChangesAsync(Ct);

        Assert.Equal(3, await context.ItemLinks.CountAsync(l => l.WorkspaceId == world.WorkspaceId, Ct));
    }

    [Fact]
    public async Task A_resolution_without_done_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            var item = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "r1", 1m);
            item.Resolution = ItemResolution.Accepted;
            context.Items.Add(item);
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("item_resolution_only_when_done", error.ConstraintName);
    }

    [Fact]
    public async Task Done_without_a_resolution_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            var item = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "r2", 1m);
            item.Status = ItemStatus.Done;
            item.ClosedAt = DateTimeOffset.UtcNow;
            context.Items.Add(item);
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("item_resolution_only_when_done", error.ConstraintName);
    }

    [Fact]
    public async Task Done_with_a_resolution_and_a_close_time_is_accepted()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var item = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "r3", 1m);
        item.Status = ItemStatus.Done;
        item.Resolution = ItemResolution.Accepted;
        item.ClosedAt = DateTimeOffset.UtcNow;
        context.Items.Add(item);

        await context.SaveChangesAsync(Ct);

        Assert.Equal(ItemStatus.Done, item.Status);
    }

    [Fact]
    public async Task Closing_without_a_closed_at_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            var item = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "r4", 1m);
            item.Status = ItemStatus.Cancelled;
            context.Items.Add(item);
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("item_closed_at_set", error.ConstraintName);
    }

    [Fact]
    public async Task Review_required_follows_the_risk_tags()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var plain = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "k1", 1m);
        var risky = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "k2", 2m);
        risky.RiskTags = ["schema", "migration"];
        context.Items.AddRange(plain, risky);
        await context.SaveChangesAsync(Ct);

        var reloaded = await context.Items
            .AsNoTracking()
            .Where(i => i.Id == plain.Id || i.Id == risky.Id)
            .ToDictionaryAsync(i => i.Id, Ct);

        Assert.False(reloaded[plain.Id].ReviewRequired);
        Assert.True(reloaded[risky.Id].ReviewRequired);
    }

    [Fact]
    public async Task A_supersedes_link_closes_the_target()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var replacement = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "s1b", 1m);
        var original = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "s1", 2m);
        context.Items.AddRange(replacement, original);
        await context.SaveChangesAsync(Ct);

        context.ItemLinks.Add(NewLink(world.WorkspaceId, replacement.Id, original.Id, LinkType.Supersedes));
        await context.SaveChangesAsync(Ct);

        var closed = await context.Items.AsNoTracking().SingleAsync(i => i.Id == original.Id, Ct);
        Assert.Equal(ItemStatus.Superseded, closed.Status);
        Assert.NotNull(closed.ClosedAt);
    }

    private static ItemLink NewLink(Guid workspaceId, Guid from, Guid to, LinkType type) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = workspaceId,
        FromItemId = from,
        ToItemId = to,
        Type = type,
    };
}

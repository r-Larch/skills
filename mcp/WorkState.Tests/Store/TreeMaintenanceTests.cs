using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkState.Core.Data;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// `ancestors` maintenance versus a move is the one place the tree can silently corrupt
/// (doc 03 Phase 1, *Seam*), so the reparent cases assert the whole subtree, not just the item.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TreeMaintenanceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Insert_fills_ancestors_depth_and_the_ref_scope()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "P1", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        var task = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "P1.1", 1m);
        context.Items.Add(task);
        await context.SaveChangesAsync(Ct);

        var stored = await context.Items.AsNoTracking().SingleAsync(i => i.Id == task.Id, Ct);
        Assert.Equal([world.ProjectId, world.RunA, phase.Id], stored.Ancestors);
        Assert.Equal(3, stored.Depth);
        Assert.Equal(world.RunA, stored.RefScopeId);

        var project = await context.Items.AsNoTracking().SingleAsync(i => i.Id == world.ProjectId, Ct);
        Assert.Empty(project.Ancestors);
        Assert.Equal(0, project.Depth);
        Assert.Equal(project.Id, project.RefScopeId);
    }

    [Fact]
    public async Task Reparenting_rewrites_ancestors_for_the_whole_subtree()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var phaseA = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "A", 1m);
        var phaseB = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "B", 2m);
        context.Items.AddRange(phaseA, phaseB);
        await context.SaveChangesAsync(Ct);

        var task = Seed.NewItem(world.WorkspaceId, phaseA.Id, "task", "A.1", 1m);
        context.Items.Add(task);
        await context.SaveChangesAsync(Ct);

        var subtask = Seed.NewItem(world.WorkspaceId, task.Id, "subtask", "A.1.a", 1m);
        context.Items.Add(subtask);
        await context.SaveChangesAsync(Ct);

        var grandchild = Seed.NewItem(world.WorkspaceId, subtask.Id, "finding", "F-1", 1m);
        context.Items.Add(grandchild);
        await context.SaveChangesAsync(Ct);

        task.ParentId = phaseB.Id;
        await context.SaveChangesAsync(Ct);

        var moved = await Seed.ReloadAsync(fixture, task.Id);
        var movedChild = await Seed.ReloadAsync(fixture, subtask.Id);
        var movedGrandchild = await Seed.ReloadAsync(fixture, grandchild.Id);

        Assert.Equal([world.ProjectId, world.RunA, phaseB.Id], moved.Ancestors);
        Assert.Equal([world.ProjectId, world.RunA, phaseB.Id, task.Id], movedChild.Ancestors);
        Assert.Equal([world.ProjectId, world.RunA, phaseB.Id, task.Id, subtask.Id], movedGrandchild.Ancestors);
        Assert.Equal(5, movedGrandchild.Depth);
    }

    [Fact]
    public async Task Moving_a_subtree_into_another_run_re_derives_the_ref_scope()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var task = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "m1", 1m);
        context.Items.Add(task);
        await context.SaveChangesAsync(Ct);

        var subtask = Seed.NewItem(world.WorkspaceId, task.Id, "subtask", "m1.a", 1m);
        context.Items.Add(subtask);
        await context.SaveChangesAsync(Ct);

        Assert.Equal(world.RunA, (await Seed.ReloadAsync(fixture, subtask.Id)).RefScopeId);

        task.ParentId = world.RunB;
        await context.SaveChangesAsync(Ct);

        Assert.Equal(world.RunB, (await Seed.ReloadAsync(fixture, task.Id)).RefScopeId);
        Assert.Equal(world.RunB, (await Seed.ReloadAsync(fixture, subtask.Id)).RefScopeId);
    }

    [Fact]
    public async Task Reparenting_an_item_under_its_own_descendant_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "X", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        var task = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "X.1", 1m);
        context.Items.Add(task);
        await context.SaveChangesAsync(Ct);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var inner = fixture.NewContext();
            var reloaded = await inner.Items.SingleAsync(i => i.Id == phase.Id, Ct);
            reloaded.ParentId = task.Id;
            await inner.SaveChangesAsync(Ct);
        });

        Assert.Contains("cyclic", error.MessageText);
    }

    [Fact]
    public async Task Inserting_between_two_siblings_writes_one_row_and_ordering_holds()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "S", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        var first = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "2.1", 1m);
        var second = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "2.2", 2m);
        context.Items.AddRange(first, second);
        await context.SaveChangesAsync(Ct);

        var before = await context.Items.CountAsync(i => i.ParentId == phase.Id, Ct);

        var inserted = Seed.NewItem(
            world.WorkspaceId,
            phase.Id,
            "task",
            "2.1b",
            SeqAllocator.Between(first.Seq, second.Seq));
        context.Items.Add(inserted);
        await context.SaveChangesAsync(Ct);

        var after = await context.Items.CountAsync(i => i.ParentId == phase.Id, Ct);
        Assert.Equal(before + 1, after);

        var ordered = await context.Items.AsNoTracking()
            .Where(i => i.ParentId == phase.Id)
            .OrderBy(i => i.Seq)
            .Select(i => i.Ref)
            .ToListAsync(Ct);

        Assert.Equal(["2.1", "2.1b", "2.2"], ordered);
        Assert.Equal(1.5m, inserted.Seq);
    }

    [Fact]
    public async Task Two_siblings_cannot_share_a_seq()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "T", 1m);
        context.Items.Add(phase);
        await context.SaveChangesAsync(Ct);

        context.Items.Add(Seed.NewItem(world.WorkspaceId, phase.Id, "task", "t1", 1m));
        await context.SaveChangesAsync(Ct);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var inner = fixture.NewContext();
            inner.Items.Add(Seed.NewItem(world.WorkspaceId, phase.Id, "task", "t2", 1m));
            await inner.SaveChangesAsync(Ct);
        });

        Assert.Equal("item_sibling_order", error.ConstraintName);
    }

    [Fact]
    public async Task Sibling_order_is_deferrable_so_a_swap_inside_one_transaction_succeeds()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        Guid phaseId;
        Guid firstId;
        Guid secondId;

        await using (var context = fixture.NewContext())
        {
            var phase = Seed.NewItem(world.WorkspaceId, world.RunA, "phase", "W", 1m);
            context.Items.Add(phase);
            await context.SaveChangesAsync(Ct);

            var first = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "w1", 1m);
            var second = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "w2", 2m);
            context.Items.AddRange(first, second);
            await context.SaveChangesAsync(Ct);

            phaseId = phase.Id;
            firstId = first.Id;
            secondId = second.Id;
        }

        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await Execute(connection, transaction, "SET CONSTRAINTS item_sibling_order DEFERRED");
            await Execute(connection, transaction, $"UPDATE item SET seq = 2 WHERE id = '{firstId}'");
            await Execute(connection, transaction, $"UPDATE item SET seq = 1 WHERE id = '{secondId}'");
            await transaction.CommitAsync(Ct);
        }

        await using var verify = fixture.NewContext();
        var ordered = await verify.Items.AsNoTracking()
            .Where(i => i.ParentId == phaseId)
            .OrderBy(i => i.Seq)
            .Select(i => i.Ref)
            .ToListAsync(Ct);

        Assert.Equal(["w2", "w1"], ordered);
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
    }
}

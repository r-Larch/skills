using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// A path is a place on one tenant's disk, not on the server's: the tree lock is keyed by workspace for a path
/// as much as for a name. The literal <c>P:/x</c> on purpose — every world is a workspace of its own, so the
/// same path held in every earlier run of this test is exactly what must not block this one.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TenantTreeLockTests(PostgresFixture fixture)
{
    private const string Tree = "P:/x";

    [Fact]
    public async Task Tenant_A_lock_on_a_path_does_not_block_tenant_B_and_still_blocks_within_A()
    {
        var a = await WriteSeed.CreateAsync(fixture);
        var b = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(a, a.PhaseId, "1.2");

        var held = await Start(a, "1.1", WriteSeed.Orchestrator);
        Assert.False(held.Refused, held.Text);

        var otherTenant = await Start(b, "1.1", WriteSeed.Other);
        Assert.False(otherTenant.Refused, otherTenant.Text);
        Assert.Equal(ItemStatus.InProgress, await b.StatusAsync("1.1"));

        var sameTenant = await Start(a, "1.2", WriteSeed.Other);
        Assert.True(sameTenant.Refused, sameTenant.Text);
        RefusalShape.SelfCorrecting(sameTenant.Text);
        Assert.Contains("run-a/1.1", sameTenant.Text, StringComparison.Ordinal);
        Assert.Contains("p:/x", sameTenant.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await a.StatusAsync("1.2"));
    }

    private static Task<WriteResult> Start(WriteWorld world, string reference, Actor actor)
        => world.Writer.StatusAsync(world.WorkspaceId, reference, "in_progress", actor, new StatusArgs(Tree: Tree), Ct);
}

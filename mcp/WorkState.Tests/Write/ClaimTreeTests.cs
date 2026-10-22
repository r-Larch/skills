using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// The 2026-10-22 decision: one writer per worktree, many per run. <c>/rlarch:controller</c> runs
/// parallel units of one run, each in a git worktree of its own — a lock on the run refused the
/// second one, so the tree the caller works in has to be what the claim locks.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClaimTreeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Two_units_of_one_run_in_different_trees_are_both_in_progress()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var second = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        var root = Root();

        var first = await Start(world, "1.1", WriteSeed.Orchestrator, $@"P:\{root}\ws-1");
        var other = await Start(world, "1.2", WriteSeed.Other, $"P:/{root}/ws-2/");

        Assert.False(first.Refused, first.Text);
        Assert.False(other.Refused, other.Text);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.2"));

        await using var db = fixture.NewContext();
        var claim = await db.Claims.SingleAsync(c => c.ItemId == second && c.ReleasedAt == null, Ct);
        Assert.Equal($"p:/{root}/ws-2", claim.Tree);
        Assert.Equal($"p:/{root}/ws-2", claim.SerializationKey);
    }

    [Fact]
    public async Task A_second_claim_on_the_same_tree_is_refused_naming_the_holder()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        var root = Root();

        var held = await Start(world, "1.1", WriteSeed.Orchestrator, $@"P:\{root.ToUpperInvariant()}\WS-1\");
        Assert.False(held.Refused, held.Text);

        var sameTree = await Start(world, "1.2", WriteSeed.Other, $"p:/{root}/ws-1");

        Assert.True(sameTree.Refused, sameTree.Text);
        RefusalShape.SelfCorrecting(sameTree.Text);
        Assert.Contains("run-a/1.1", sameTree.Text, StringComparison.Ordinal);
        Assert.Contains(WriteSeed.Orchestrator.Name, sameTree.Text, StringComparison.Ordinal);
        Assert.Contains("work_status(ref:\"run-a/1.1\", to:\"review\")", sameTree.Text, StringComparison.Ordinal);
        Assert.Contains($"tree:\"p:/{root}/ws-1\"", sameTree.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.2"));
    }

    /// <summary>
    /// The refusal a controller hits today, before it learns the parameter: the run's tree is held,
    /// so the route that gets it moving is the one naming its own worktree.
    /// </summary>
    [Fact]
    public async Task A_refusal_without_a_tree_names_the_tree_route()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        await Start(world, "1.1", WriteSeed.Orchestrator, tree: null);
        var refused = await Start(world, "1.2", WriteSeed.Other, tree: null);

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains(
            "work_status(ref:\"1.2\", to:\"in_progress\", tree:\"<your worktree path>\")",
            refused.Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a run key or a repo root the lock falls back to the ref scope — a name, and every
    /// workspace has a <c>run-a</c>. It used to be one server-wide lock.
    /// </summary>
    [Fact]
    public async Task Same_named_runs_in_two_workspaces_without_a_tree_do_not_collide()
    {
        var first = await WriteSeed.CreateAsync(fixture, serializationKey: "");
        var second = await WriteSeed.CreateAsync(fixture, serializationKey: "");

        var held = await Start(first, "1.1", WriteSeed.Orchestrator, tree: null);
        var other = await Start(second, "1.1", WriteSeed.Other, tree: null);

        Assert.False(held.Refused, held.Text);
        Assert.False(other.Refused, other.Text);
        Assert.Equal(ItemStatus.InProgress, await second.StatusAsync("1.1"));
    }

    [Fact]
    public async Task An_expired_claim_on_a_tree_is_retaken_on_that_tree()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var tree = $"p:/{Root()}/ws-1";

        var held = await Start(world, "1.1", WriteSeed.Orchestrator, tree.Replace('/', '\\'));
        Assert.False(held.Refused, held.Text);
        await WriteSeed.ExpireClaimAsync(world, world.TaskId);

        var lines = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);
        var line = Assert.Single(lines, l => l.Ref == "1.1" && l.What.Contains("claim by", StringComparison.Ordinal));

        Assert.Contains($"on tree \"{tree}\"", line.What, StringComparison.Ordinal);
        Assert.Equal($"work_status(ref:\"1.1\", to:\"in_progress\", tree:\"{tree}\") to retake it", line.Call);
    }

    [Theory]
    [InlineData(@"P:\Projects\Skills.worktrees\ws-8\", "p:/projects/skills.worktrees/ws-8", true)]
    [InlineData("p:/projects/skills.worktrees/ws-8", "p:/projects/skills.worktrees/ws-8", true)]
    [InlineData(@"C:\", "c:/", true)]
    [InlineData(@"\\Server\Share\Tree", "//server/share/tree", true)]
    [InlineData("/home/Dev/Tree/", "/home/Dev/Tree", true)]
    [InlineData("  ws/8-claim-per-tree ", "ws/8-claim-per-tree", false)]
    [InlineData("refs/heads/ws/8", "ws/8", false)]
    public void A_tree_has_one_spelling(string tree, string key, bool isPath)
        => Assert.Equal(new WorkingTree(key, isPath), WorkingTree.Parse(tree));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_tree_is_no_tree(string? tree) => Assert.Null(WorkingTree.Parse(tree));

    /// <summary>A path lock is server-wide, and every test in the collection shares one server.</summary>
    private static string Root() => $"wt-{Guid.NewGuid():n}"[..15];

    private static Task<WriteResult> Start(WriteWorld world, string reference, Actor actor, string? tree)
        => world.Writer.StatusAsync(
            world.WorkspaceId, reference, "in_progress", actor, new StatusArgs(Tree: tree), Ct);
}

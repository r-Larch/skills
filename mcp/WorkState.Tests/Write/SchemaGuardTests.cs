using Dapper;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 07 §1 read from the agent's side: a rule Postgres owns can only say no, but it must say no
/// as a refusal with a route — never as "An error occurred invoking …". Each test drives one guard
/// into firing for real, including the ones only a concurrent writer can reach.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SchemaGuardTests(PostgresFixture fixture)
{
    /// <summary>
    /// The live 2026-10-22 failure: one agent, one run, a second item started while the first still
    /// holds the tree. The pre-check skipped claims under the caller's own name, so the unique index
    /// answered instead — as an unhandled exception.
    /// </summary>
    [Fact]
    public async Task One_agent_starting_a_second_item_of_its_tree_is_refused_naming_the_holder()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        var held = await Start(world, "1.1", WriteSeed.Orchestrator);
        Assert.False(held.Refused, held.Text);

        var second = await Start(world, "1.2", WriteSeed.Orchestrator);

        Assert.True(second.Refused, second.Text);
        RefusalShape.SelfCorrecting(second.Text);
        Assert.Contains("run-a/1.1", second.Text, StringComparison.Ordinal);
        Assert.Contains(WriteSeed.Orchestrator.Name, second.Text, StringComparison.Ordinal);
        Assert.Contains("lapses in", second.Text, StringComparison.Ordinal);
        Assert.Contains("work_status(ref:\"run-a/1.1\", to:\"review\")", second.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.2"));
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
    }

    /// <summary>
    /// The race the pre-check cannot close: another writer's claim commits between this call's read
    /// and its insert. Held open deterministically — the insert blocks on the index until the
    /// competing transaction commits, then the index refuses it.
    /// </summary>
    [Fact]
    public async Task A_claim_committed_between_the_check_and_the_insert_is_refused_naming_the_holder()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var rival = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        await using var db = fixture.NewContext();
        await using var competing = await db.Database.BeginTransactionAsync(Ct);
        db.Claims.Add(new Claim
        {
            Id = Guid.CreateVersion7(),
            ItemId = rival,
            SerializationKey = $"tree-{world.RunId:n}",
            Writes = true,
            AgentName = WriteSeed.Other.Name,
            Role = ActorRole.Orchestrator,
            AcquiredAt = DateTimeOffset.UtcNow,
            HeartbeatAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        });
        await db.SaveChangesAsync(Ct);

        var start = Start(world, "1.1", WriteSeed.Orchestrator);
        await BlockedOnALockAsync(start);
        await competing.CommitAsync(Ct);

        var refused = await start;

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("run-a/1.2", refused.Text, StringComparison.Ordinal);
        Assert.Contains(WriteSeed.Other.Name, refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
    }

    /// <summary>
    /// The index is server-wide but the sweep is per workspace, so a lapsed claim another workspace
    /// has not swept still holds a shared key. The lease filter used to hide it from the pre-check.
    /// </summary>
    [Fact]
    public async Task A_lapsed_claim_another_workspace_has_not_swept_is_a_refusal_not_an_exception()
    {
        var key = $"shared-{Guid.CreateVersion7():n}";
        var first = await WriteSeed.CreateAsync(fixture, key);
        var second = await WriteSeed.CreateAsync(fixture, key);

        await Start(first, "1.1", WriteSeed.Orchestrator);
        await WriteSeed.ExpireClaimAsync(first, first.TaskId);

        var refused = await Start(second, "1.1", WriteSeed.Other);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("another workspace", refused.Text, StringComparison.Ordinal);
        Assert.Contains("lapsed", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await second.StatusAsync("1.1"));
    }

    /// <summary>
    /// item_ref_unique, reached by work_status → deferred: the follow-up's ref was checked free in the
    /// source run's numbering and then inserted into the target run's, where it was taken.
    /// </summary>
    [Fact]
    public async Task Deferring_into_another_run_numbers_the_follow_up_in_that_run()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        var runB = (await world.ItemAsync("run-b")).Id;
        await WriteSeed.ChildAsync(world, runB, "1.1-d", kind: "followup");

        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "belongs to the other run", Target: "run-b"), Ct);

        Assert.False(moved.Refused, moved.Text);
        Assert.Contains("1.1-d2", moved.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
    }

    /// <summary>
    /// The guards a write tool can be driven into on purpose, each through the writer: a duplicate
    /// ref, a cyclic reparent and a self-link. (A deferral to "backlog" past a cancelled in-run
    /// backlog phase was a fourth until ws/12 made "backlog" a project-level run; it now succeeds —
    /// BacklogCarryTests.)
    /// </summary>
    [Fact]
    public async Task Every_schema_guard_a_write_tool_can_reach_on_purpose_is_a_routed_refusal()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var writer = world.Writer;
        var id = world.WorkspaceId;

        WriteResult[] refusals =
        [
            await writer.CreateAsync(
                id, WriteSeed.Orchestrator,
                [new NewItemArg("phase", "a second phase 1", Ref: "1", Parent: "run-a")], Ct),
            await writer.MoveAsync(id, WriteSeed.Orchestrator, "1", "1.1", null, null, Ct),
            await writer.LinkAsync(id, WriteSeed.Orchestrator, "1.1", "1.1", "blocks", false, Ct),
        ];

        foreach (var refusal in refusals)
        {
            Assert.True(refusal.Refused, $"should have been refused:\n{refusal.Text}");
            RefusalShape.SelfCorrecting(refusal.Text);
        }

        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
    }

    /// <summary>
    /// The transport's net, over the real MCP channel: constraint_ref_unique taken by a concurrent
    /// work_constrain between the allocator's read and the insert. No writer path catches that one,
    /// so the tool must.
    /// </summary>
    [Fact]
    public async Task A_guard_only_a_concurrent_writer_reaches_still_comes_back_as_a_refusal()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        await using var db = fixture.NewContext();
        await using var competing = await db.Database.BeginTransactionAsync(Ct);
        db.ConstraintRules.Add(new ConstraintRule
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            Ref = "C1",
            Kind = ConstraintKind.StackFact,
            Text = "written by the other orchestrator",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);

        var call = ReadServer.CallEitherAsync(
            client,
            "work_constrain",
            new Dictionary<string, object?> { ["kind"] = "stack_fact", ["text"] = "the stack is .NET 10" },
            Ct);

        await BlockedOnALockAsync(call);
        await competing.CommitAsync(Ct);

        var (text, refused) = await call;

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("work_constrain(", text, StringComparison.Ordinal);
    }

    private async Task BlockedOnALockAsync(Task call)
    {
        const string sql =
            """
            SELECT count(*)::int FROM pg_stat_activity
            WHERE  datname = current_database() AND wait_event_type = 'Lock';
            """;

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);

        for (var i = 0; i < 200; i++)
        {
            Assert.False(call.IsCompleted, "the call finished before it reached the competing row");

            if (await connection.ExecuteScalarAsync<int>(sql) > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("the call never blocked on the competing row's lock");
    }

    private static Task<WriteResult> Start(WriteWorld world, string reference, Actor actor)
        => world.Writer.StatusAsync(world.WorkspaceId, reference, "in_progress", actor, new StatusArgs(), Ct);
}

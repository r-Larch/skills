using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// The write tools over the real MCP HTTP transport, with real bearer tokens — the same path Claude
/// Code takes. What is asserted here and nowhere else is the part that only exists once a token is
/// involved: the actor, and <c>work_verify</c>'s <c>run_by</c>.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class WriteToolTests(PostgresFixture fixture)
{
    /// <summary>
    /// doc 01 §6 — the entire reason a worker may hold <c>work_verify</c>. The argument asks for an
    /// orchestrator run; the token says worker; the row says worker; and the item therefore stays
    /// closed to the agent that built it.
    /// </summary>
    [Fact]
    public async Task A_worker_token_cannot_record_an_orchestrator_verification_however_it_asks()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var worker = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var (text, refused) = await ReadServer.CallEitherAsync(
            worker,
            "work_verify",
            new Dictionary<string, object?>
            {
                ["ref"] = "1.1",
                ["result_line"] = "Passed! Failed: 0, Passed: 91",
                ["run_by"] = "orchestrator",
            },
            Ct);

        Assert.False(refused, text);
        Assert.Contains("by worker", text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.All(
            db.Verifications.Where(v => v.ItemId == world.TaskId).ToList(),
            v => Assert.Equal(VerifyRunner.Worker, v.RunBy));
    }

    [Fact]
    public async Task A_refused_close_comes_back_as_an_error_the_model_can_act_on()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.CriterionAsync(world, world.TaskId, 2, "DeletesSkipped surfaced", ticked: false);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var (text, refused) = await ReadServer.CallEitherAsync(
            client,
            "work_status",
            new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "done" },
            Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        RefusalShape.NamesTheOverride(text);
        Assert.Equal(2, RefusalShape.Conditions(text));
    }

    /// <summary>
    /// The whole spine of <c>ServerInstructions</c>, driven through the transport: create, claim,
    /// verify, close. If any printed <c>Next:</c> line names a call that does not work from the
    /// role that was just told to make it, this is where it shows.
    /// </summary>
    [Fact]
    public async Task The_loop_the_server_instructions_describe_runs_end_to_end_on_an_empty_server()
    {
        var slug = $"loop-{Guid.CreateVersion7():n}";
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var onboarding = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        Assert.Contains("NEXT   work_create(", onboarding, StringComparison.Ordinal);

        await Ok(client, "work_create", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["kind"] = "project", ["ref"] = "gaia", ["title"] = "Gaia" },
            },
        });

        await Ok(client, "work_create", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["kind"] = "run", ["ref"] = "entity-store", ["parent"] = "gaia", ["title"] = "Entity store",
                },
            },
        });

        await Ok(client, "work_create", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["kind"] = "task",
                    ["ref"] = "1.1",
                    ["parent"] = "entity-store",
                    ["title"] = "EntityProjector",
                    ["verify_cmd"] = "dotnet test --filter Projector",
                    ["criteria"] = new[] { "facets are replaced per entity per run" },
                },
            },
        });

        var frontier = await ReadServer.CallAsync(client, "work_next", null, Ct);
        Assert.Contains("1.1", frontier, StringComparison.Ordinal);

        await Ok(client, "work_status", new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "in_progress" });

        // Closing here must be refused: the criterion is open and nothing has been verified.
        var (refusal, refused) = await ReadServer.CallEitherAsync(
            client, "work_status", new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "done" }, Ct);

        Assert.True(refused, refusal);
        RefusalShape.SelfCorrecting(refusal);

        await Ok(client, "work_update", new Dictionary<string, object?>
        {
            ["ref"] = "1.1",
            ["criteria"] = new object[]
            {
                new Dictionary<string, object?> { ["id"] = "c1", ["checked"] = true, ["evidence"] = "asserted" },
            },
        });

        await Ok(client, "work_verify", new Dictionary<string, object?>
        {
            ["ref"] = "1.1",
            ["command"] = "dotnet test --filter Projector",
            ["result_line"] = "Passed! Failed: 0, Passed: 187",
        });

        var closed = await Ok(client, "work_status", new Dictionary<string, object?>
        {
            ["ref"] = "1.1",
            ["to"] = "done",
            ["commit"] = "a1b2c3d",
        });

        Assert.Contains("→ done", closed, StringComparison.Ordinal);

        var tree = await ReadServer.CallAsync(client, "work_tree", null, Ct);
        Assert.Contains("done", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deferral_through_the_transport_leaves_an_item_the_frontier_returns()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        await Ok(client, "work_status", new Dictionary<string, object?>
        {
            ["ref"] = "1.1",
            ["to"] = "deferred",
            ["target"] = "backlog",
            ["reason"] = "belongs in the next run",
        });

        var frontier = await ReadServer.CallAsync(client, "work_next", null, Ct);

        Assert.Contains("1.1-d", frontier, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_claim_is_swept_by_work_brief_and_reported_in_attention()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        await Ok(client, "work_status", new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "in_progress" });
        await WriteSeed.ExpireClaimAsync(world, world.TaskId);

        await ReadServer.CallAsync(client, "work_brief", null, Ct);

        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));
        Assert.Contains("claim_expired", await WriteSeed.EventKindsAsync(world, world.TaskId));
    }

    private static async Task<string> Ok(
        ModelContextProtocol.Client.McpClient client,
        string tool,
        Dictionary<string, object?> arguments)
    {
        var (text, refused) = await ReadServer.CallEitherAsync(client, tool, arguments, Ct);
        Assert.False(refused, $"{tool} was refused:\n{text}");
        return text;
    }
}

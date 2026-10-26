using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using WorkState.Core.Domain;
using WorkState.Mcp;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 11 over the real transport. Two controller sessions shared one token, so all 301 live events
/// and 26 claims were signed "controller"; the session is the second label that tells them apart,
/// and it is the unit "one writer per tree" is now counted in.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SessionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Two_sessions_on_one_token_get_separate_rows_and_separate_attribution()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        await using var first = await ConnectAsync(server, ReadServer.ControllerToken, Agent("skills-5d"));
        await using var second = await ConnectAsync(server, ReadServer.ControllerToken, Agent("autowork-7c"));

        await StartAsync(first, "1.1", Tree());
        await StartAsync(second, "1.2", Tree());

        await using var db = fixture.NewContext();
        var sessions = await db.AgentSessions.Where(s => s.WorkspaceId == world.WorkspaceId).ToListAsync(Ct);
        Assert.Equal(["autowork-7c", "skills-5d"], sessions.Select(s => s.Name).Order());
        Assert.All(sessions, s => Assert.Equal(ActorRole.Controller, s.Role));

        var byName = sessions.ToDictionary(s => s.Name!, s => s.Id);
        var claims = await LiveClaimsAsync(world);
        Assert.Equal(byName["skills-5d"], Assert.Single(claims, c => c.ItemId == world.TaskId).SessionId);
        Assert.Equal(byName["autowork-7c"], Assert.Single(claims, c => c.ItemId != world.TaskId).SessionId);

        var events = await db.Events
            .Where(e => e.WorkspaceId == world.WorkspaceId && e.Kind == "claim_acquired")
            .ToListAsync(Ct);
        Assert.Equal(byName.Values.Order(), events.Select(e => e.SessionId!.Value).Order());
        Assert.All(events, e => Assert.Equal("controller", e.Actor));

        await using var reader = await server.ConnectAsync(ReadServer.ControllerToken, Ct);
        var history = await ReadServer.CallAsync(reader, "work_history", null, Ct);
        Assert.Contains("skills-5d", history, StringComparison.Ordinal);
        Assert.Contains("autowork-7c", history, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard for everything registered before doc 11: no header, no session. The claim, the
    /// event and the refusal read exactly as they did, and the tree is held per writer as before.
    /// </summary>
    [Fact]
    public async Task An_unnamed_call_has_no_session_and_is_attributed_as_before()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var tree = Tree();
        await StartAsync(client, "1.1", tree);
        var (text, refused) = await StatusAsync(client, "1.2", tree);

        Assert.True(refused, text);
        Assert.Contains("held by controller — that is you", text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.False(await db.AgentSessions.AnyAsync(s => s.WorkspaceId == world.WorkspaceId, Ct));
        Assert.Null(Assert.Single(await LiveClaimsAsync(world)).SessionId);
        Assert.All(
            await db.Events.Where(e => e.WorkspaceId == world.WorkspaceId).ToListAsync(Ct),
            e =>
            {
                Assert.Null(e.SessionId);
                Assert.Equal("controller", e.Actor);
            });
    }

    [Fact]
    public async Task An_unnamed_brief_says_how_to_sign_and_a_named_one_does_not()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var unnamed = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);
        await using var named = await ConnectAsync(server, ReadServer.OrchestratorToken, Agent("aw-m4"));

        const string hint = "session  unnamed — /rlarch:workstate-setup installs the session helper";

        var bare = (await ReadServer.CallAsync(unnamed, "work_brief", null, Ct)).Split('\n');
        Assert.StartsWith(hint, bare[1], StringComparison.Ordinal);
        Assert.All(bare, line => Assert.True(line.Length <= 100, $"{line.Length} columns: {line}"));

        var signed = await ReadServer.CallAsync(named, "work_brief", null, Ct);
        Assert.DoesNotContain("session  unnamed", signed, StringComparison.Ordinal);

        // A write answers an unnamed caller without comment.
        var (wrote, _) = await StatusAsync(unnamed, "1.1", Tree());
        Assert.DoesNotContain("X-WorkState-Agent", wrote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registration_headers_fill_the_session_row()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await ConnectAsync(
            server,
            ReadServer.WorkerToken,
            Agent(
                "aw-m3",
                (CallerSession.SessionHeader, "0b5e7c1a-claude"),
                (CallerSession.LaunchedByHeader, "foreman"),
                (CallerSession.OriginHeader, "foreman/nomos/Nomos"),
                (CallerSession.CwdHeader, "P:/worktrees/m3"),
                (CallerSession.ForemanRoleHeader, "repo")));

        await ReadServer.CallAsync(client, "work_brief", null, Ct);

        await using var db = fixture.NewContext();
        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("aw-m3", session.Name);
        Assert.Equal("0b5e7c1a-claude", session.ClaudeSessionId);
        Assert.Equal("foreman", session.LaunchedBy);
        Assert.Equal("foreman/nomos/Nomos", session.Origin);
        Assert.Equal("P:/worktrees/m3", session.Cwd);
        Assert.Equal("repo", session.ExternalRole);
        Assert.Equal(ActorRole.Worker, session.Role);
    }

    /// <summary>
    /// <c>--resume</c>, compaction and a respawn keep the name and open a new MCP connection; a
    /// respawn can come back under a new Claude session id. Keyed by anything but (workspace, name),
    /// the resumed session would be a stranger to its own claim and refused its own tree.
    /// </summary>
    [Fact]
    public async Task A_resumed_session_keeps_its_claim_and_its_tree()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var tree = Tree();

        await using (var original = await ConnectAsync(
                         server, ReadServer.OrchestratorToken, Agent("aw-m1", (CallerSession.SessionHeader, "claude-a"))))
        {
            await StartAsync(original, "1.1", tree);
        }

        await using var resumed = await ConnectAsync(
            server, ReadServer.OrchestratorToken, Agent("aw-m1", (CallerSession.SessionHeader, "claude-b")));

        var (text, refused) = await StatusAsync(resumed, "1.2", tree);
        Assert.False(refused, text);

        await using var db = fixture.NewContext();
        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("claude-b", session.ClaudeSessionId);
        Assert.Equal(WorkingTreeKey(tree), session.Tree);

        var claims = await LiveClaimsAsync(world);
        Assert.Equal(2, claims.Count);
        Assert.All(claims, c => Assert.Equal(session.Id, c.SessionId));

        // The Claude session id under one name changed: kept as history, not overwritten silently.
        var changed = await db.Events.SingleAsync(
            e => e.WorkspaceId == world.WorkspaceId && e.Kind == "session_claude_id_changed", Ct);
        Assert.Contains("claude-a", changed.Payload, StringComparison.Ordinal);
        Assert.Equal(session.Id, changed.SessionId);
    }


    /// <summary>
    /// doc 11 — the controller holds a worktree and a nested agent works two of its tasks there. The
    /// old rule refused the second claim because one agent was already writing in that tree.
    /// </summary>
    [Fact]
    public async Task The_same_session_may_claim_two_items_in_the_tree_it_holds()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await ConnectAsync(server, ReadServer.ControllerToken, Agent("skills-5d"));

        var tree = Tree();
        await StartAsync(client, "1.1", tree);
        var (text, refused) = await StatusAsync(client, "1.2", tree);

        Assert.False(refused, text);
        Assert.Contains("acquired by skills-5d", text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.2"));
        Assert.Equal(2, (await LiveClaimsAsync(world)).Count);
    }

    [Fact]
    public async Task A_different_session_is_refused_naming_the_holder()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var holder = await ConnectAsync(server, ReadServer.ControllerToken, Agent("skills-5d"));
        await using var other = await ConnectAsync(server, ReadServer.ControllerToken, Agent("autowork-7c"));

        var tree = Tree();
        await StartAsync(holder, "1.1", tree);
        var (text, refused) = await StatusAsync(other, "1.2", tree);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("held by skills-5d", text, StringComparison.Ordinal);
        Assert.Contains("run-a/1.1", text, StringComparison.Ordinal);

        // A session with a name of its own is not told the holder might be itself.
        Assert.DoesNotContain("work_brief(agent:", text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.2"));

        var tree2 = await ReadServer.CallAsync(holder, "work_tree", null, Ct);
        Assert.Contains("held by skills-5d", tree2, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 30-minute lease lapsed under a multi-hour unit and the inbox called healthy work "claim
    /// expired". Any call from the claiming session — a read included — now renews it.
    /// </summary>
    [Fact]
    public async Task Any_call_from_the_claiming_session_renews_its_leases()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await ConnectAsync(server, ReadServer.OrchestratorToken, Agent("aw-lease"));
        await using var stranger = await ConnectAsync(server, ReadServer.OrchestratorToken, Agent("aw-other"));

        await StartAsync(client, "1.1", Tree());
        await BackdateAsync(world);

        await ReadServer.CallAsync(stranger, "work_search", new Dictionary<string, object?> { ["q"] = "task" }, Ct);
        Assert.True((await LiveClaimsAsync(world)).Single().ExpiresAt < DateTimeOffset.UtcNow, "another session renewed it");

        await ReadServer.CallAsync(client, "work_search", new Dictionary<string, object?> { ["q"] = "task" }, Ct);

        var claim = (await LiveClaimsAsync(world)).Single();
        Assert.True(claim.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(20), $"lease ends {claim.ExpiresAt:O}");
        Assert.True(claim.HeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task A_name_held_by_another_launcher_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        await using var first = await ConnectAsync(
            server, ReadServer.WorkerToken, Agent("aw-dup", ("X-WorkState-Launched-By", "foreman-a")));
        await ReadServer.CallAsync(first, "work_brief", null, Ct);

        await using var second = await ConnectAsync(
            server, ReadServer.WorkerToken, Agent("aw-dup", ("X-WorkState-Launched-By", "foreman-b")));
        var (text, refused) = await ReadServer.CallEitherAsync(second, "work_brief", null, Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("\"aw-dup\"", text, StringComparison.Ordinal);
        Assert.Contains("foreman-a", text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("foreman-a", session.LaunchedBy);
    }

    /// <summary>
    /// The guard of doc 01 §6, restated for doc 11: nothing a session says about itself reaches the
    /// role — not a Foreman role header, not a controller's name. Passes before sessions existed and after.
    /// </summary>
    [Fact]
    public async Task No_header_raises_a_role()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        await using var controller = await ConnectAsync(server, ReadServer.ControllerToken, Agent("skills-5d"));
        await ReadServer.CallAsync(controller, "work_brief", null, Ct);

        await using var worker = await ConnectAsync(
            server,
            ReadServer.WorkerToken,
            Agent("skills-5d", ("X-WorkState-Foreman-Role", "platform"), ("X-WorkState-Origin", "foreman/x")));

        await ReadServer.CallEitherAsync(worker, "work_brief", null, Ct);

        var tools = (await worker.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.DoesNotContain("work_status", tools);
        Assert.DoesNotContain("work_create", tools);

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
        var verification = await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct);
        Assert.Equal(VerifyRunner.Worker, verification.RunBy);

        var journal = await db.Events.SingleAsync(e => e.ItemId == world.TaskId && e.Kind == "verified", Ct);
        Assert.Equal(ActorRole.Worker, journal.Role);
    }

    private static (string, string)[] Agent(string name, params (string, string)[] more)
        => [(CallerSession.AgentHeader, name), .. more];

    private static Task<McpClient> ConnectAsync(ReadServer server, string token, (string Name, string Value)[] headers)
    {
        var all = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        foreach (var (name, value) in headers)
        {
            all[name] = value;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{server.BaseAddress}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = all,
        });

        return McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task StartAsync(McpClient client, string reference, string tree)
    {
        var (text, refused) = await StatusAsync(client, reference, tree);
        Assert.False(refused, text);
    }

    private static Task<(string Text, bool Refused)> StatusAsync(McpClient client, string reference, string tree)
        => ReadServer.CallEitherAsync(
            client,
            "work_status",
            new Dictionary<string, object?> { ["ref"] = reference, ["to"] = "in_progress", ["tree"] = tree },
            Ct);

    private async Task<List<Claim>> LiveClaimsAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return await db.Claims
            .Where(c => c.ReleasedAt == null && db.Items.Any(i => i.Id == c.ItemId && i.WorkspaceId == world.WorkspaceId))
            .ToListAsync(Ct);
    }

    private async Task BackdateAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        foreach (var claim in await db.Claims
                     .Where(c => c.ReleasedAt == null && db.Items.Any(i => i.Id == c.ItemId && i.WorkspaceId == world.WorkspaceId))
                     .ToListAsync(Ct))
        {
            claim.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            claim.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-35);
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A path lock is server-wide, and every test in the collection shares one server.</summary>
    private static string Tree() => $"p:/wt-{Guid.NewGuid():n}"[..18];

    private static string WorkingTreeKey(string tree) => Core.Writing.WorkingTree.Parse(tree)!.Key;
}

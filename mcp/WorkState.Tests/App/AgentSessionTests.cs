using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ModelContextProtocol.Client;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Mcp;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 14 §2 — per-session tokens. A launcher mints a token for one named session; the token carries
/// its role, never more than its minter's, and its session, so the name is bound to the credential
/// rather than asserted by a header.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AgentSessionTests(PostgresFixture fixture)
{
    private const string BeforeTokens = "20261023115730_Sessions";
    private const string BeforeSlidingExpiry = "20261023220548_AgentSessionTokens";

    [Fact]
    public async Task A_controller_mints_a_token_returned_once_and_the_row_keeps_only_its_hash()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (status, body) = await MintAsync(server, ReadServer.ControllerToken, new
        {
            name = "aw-mint",
            role = "worker",
            launchedBy = "skills-5d",
            origin = "agents/launch",
            cwd = "P:/worktrees/mint",
            externalRole = "repo",
        });

        Assert.Equal(HttpStatusCode.Created, status);
        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith(SessionToken.Prefix, token, StringComparison.Ordinal);
        Assert.Equal("aw-mint", body.GetProperty("name").GetString());
        Assert.Equal("worker", body.GetProperty("role").GetString());

        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddHours(71), DateTimeOffset.UtcNow.AddHours(73));

        await using var db = fixture.NewContext();
        var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal(body.GetProperty("sessionId").GetGuid(), row.Id);
        Assert.Equal(SessionToken.Hash(token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(ActorRole.Worker, row.GrantedRole);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.MintedBySessionId);
        Assert.Equal("skills-5d", row.LaunchedBy);
        Assert.Equal("agents/launch", row.Origin);
        Assert.Equal("P:/worktrees/mint", row.Cwd);
        Assert.Equal("repo", row.ExternalRole);

        // Nothing stores the token in the clear: not the row, not the journal.
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM agent_session s WHERE s.workspace_id = @workspace AND s::text LIKE '%' || @token || '%')
                OR EXISTS (SELECT 1 FROM event e WHERE e.workspace_id = @workspace AND e::text LIKE '%' || @token || '%');
            """,
            new { workspace = world.WorkspaceId, token }));
    }

    [Fact]
    public async Task A_worker_cannot_mint()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (status, _) = await MintAsync(server, ReadServer.WorkerToken, new { name = "aw-w", role = "worker" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        await AssertNoSessionAsync(world);
    }

    [Fact]
    public async Task A_role_is_never_raised()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (raised, body) = await MintAsync(server, ReadServer.OrchestratorToken, new { name = "aw-up", role = "controller" });
        Assert.Equal(HttpStatusCode.Forbidden, raised);
        Assert.Contains("never raised", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        await AssertNoSessionAsync(world);

        // A minted orchestrator is held to the same rule as the static one.
        var (_, orchestrator) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-orch", role = "orchestrator" });
        var (viaMinted, _) = await MintAsync(server, Token(orchestrator), new { name = "aw-up2", role = "controller" });
        Assert.Equal(HttpStatusCode.Forbidden, viaMinted);

        var (equal, _) = await MintAsync(server, ReadServer.OrchestratorToken, new { name = "aw-same", role = "orchestrator" });
        var (lower, _) = await MintAsync(server, Token(orchestrator), new { name = "aw-child", role = "worker" });
        Assert.Equal(HttpStatusCode.Created, equal);
        Assert.Equal(HttpStatusCode.Created, lower);

        await using var db = fixture.NewContext();
        var child = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId && s.Name == "aw-child", Ct);
        Assert.Equal(orchestrator.GetProperty("sessionId").GetGuid(), child.MintedBySessionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(721)]
    public async Task The_ttl_is_bounded(int ttlHours)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (status, _) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-ttl", role = "worker", ttlHours });

        Assert.Equal(HttpStatusCode.BadRequest, status);

        var (max, body) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-ttl", role = "worker", ttlHours = 720 });
        Assert.Equal(HttpStatusCode.Created, max);
        Assert.InRange(
            body.GetProperty("expiresAt").GetDateTimeOffset(),
            DateTimeOffset.UtcNow.AddHours(719),
            DateTimeOffset.UtcNow.AddHours(721));
    }

    /// <summary>
    /// One live token per name; replace swaps the token and nothing else, so a restarted agent keeps
    /// one row, one session id and one continuous history.
    /// </summary>
    [Fact]
    public async Task A_second_mint_is_409_and_replace_swaps_the_token_on_the_same_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, first) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-twice", role = "worker" });
        var sessionId = first.GetProperty("sessionId").GetGuid();

        await using (var before = await server.ConnectAsync(Token(first), Ct))
        {
            await VerifyAsync(before, "before");
        }

        var (again, conflict) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-twice", role = "worker" });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Contains("replace", conflict.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(first)));

        var (replaced, second) = await MintAsync(
            server, ReadServer.ControllerToken, new { name = "aw-twice", role = "worker", replace = true });
        Assert.Equal(HttpStatusCode.Created, replaced);
        Assert.Equal(sessionId, second.GetProperty("sessionId").GetGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(first)));
        await using (var after = await server.ConnectAsync(Token(second), Ct))
        {
            await VerifyAsync(after, "after");
        }

        await using var db = fixture.NewContext();
        Assert.Single(await db.AgentSessions.Where(s => s.WorkspaceId == world.WorkspaceId).ToListAsync(Ct));

        var verified = await db.Events
            .Where(e => e.ItemId == world.TaskId && e.Kind == "verified")
            .ToListAsync(Ct);
        Assert.Equal(2, verified.Count);
        Assert.All(verified, e => Assert.Equal(sessionId, e.SessionId));
    }

    [Fact]
    public async Task A_minted_worker_token_sees_the_worker_tools_and_writes_as_its_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-worker", role = "worker" });

        await using var asStatic = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        await using var asMinted = await server.ConnectAsync(Token(minted), Ct);

        Assert.Equal(await ToolsAsync(asStatic), await ToolsAsync(asMinted));

        var text = await VerifyAsync(asMinted, "minted");
        Assert.Contains("by worker", text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var verification = await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct);
        Assert.Equal(VerifyRunner.Worker, verification.RunBy);

        var journal = await db.Events.SingleAsync(e => e.ItemId == world.TaskId && e.Kind == "verified", Ct);
        Assert.Equal(ActorRole.Worker, journal.Role);
        Assert.Equal(minted.GetProperty("sessionId").GetGuid(), journal.SessionId);

        await using var reader = await server.ConnectAsync(ReadServer.ControllerToken, Ct);
        var history = await ReadServer.CallAsync(reader, "work_history", null, Ct);
        Assert.Contains("aw-worker", history, StringComparison.Ordinal);
    }

    /// <summary>The nesting of the static tokens holds for minted ones: a controller sees what all three see.</summary>
    [Fact]
    public async Task Minted_roles_nest_as_the_static_ones_do()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        foreach (var (role, staticToken) in new[]
                 {
                     ("orchestrator", ReadServer.OrchestratorToken),
                     ("controller", ReadServer.ControllerToken),
                 })
        {
            var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = $"aw-{role}", role });

            await using var asStatic = await server.ConnectAsync(staticToken, Ct);
            await using var asMinted = await server.ConnectAsync(Token(minted), Ct);

            Assert.Equal(await ToolsAsync(asStatic), await ToolsAsync(asMinted));
        }
    }

    [Fact]
    public async Task A_minted_token_ignores_the_agent_header()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-bound", role = "worker" });

        await using var client = await server.ConnectAsync(
            Token(minted),
            Ct,
            new Dictionary<string, string>
            {
                [CallerSession.AgentHeader] = "skills-5d",
                [CallerSession.LaunchedByHeader] = "someone-else",
            });

        var brief = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        Assert.DoesNotContain("session  unnamed", brief, StringComparison.Ordinal);
        await VerifyAsync(client, "bound");

        await using var db = fixture.NewContext();
        var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("aw-bound", row.Name);

        var journal = await db.Events.SingleAsync(e => e.ItemId == world.TaskId && e.Kind == "verified", Ct);
        Assert.Equal(row.Id, journal.SessionId);
    }

    [Fact]
    public async Task Patching_the_claude_session_keeps_the_change_as_history()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(
            server, ReadServer.ControllerToken, new { name = "aw-patch", role = "worker", claudeSessionId = "claude-a" });
        var id = minted.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await PatchAsync(server, ReadServer.ControllerToken, id, "claude-a"));
        Assert.Equal(HttpStatusCode.NoContent, await PatchAsync(server, ReadServer.ControllerToken, id, "claude-b"));

        await using var db = fixture.NewContext();
        Assert.Equal("claude-b", (await db.AgentSessions.SingleAsync(s => s.Id == id, Ct)).ClaudeSessionId);

        var changed = await db.Events.SingleAsync(
            e => e.WorkspaceId == world.WorkspaceId && e.Kind == "session_claude_id_changed", Ct);
        Assert.Contains("claude-a", changed.Payload, StringComparison.Ordinal);
        Assert.Contains("claude-b", changed.Payload, StringComparison.Ordinal);
        Assert.Equal(id, changed.SessionId);

        Assert.Equal(HttpStatusCode.NotFound, await PatchAsync(server, ReadServer.ControllerToken, Guid.CreateVersion7(), "x"));
    }

    /// <summary>"The same principal, or a controller" — a session a controller minted is not an orchestrator's to change.</summary>
    [Fact]
    public async Task Only_the_minter_or_a_controller_may_change_a_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, byController) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-c", role = "worker" });
        var (_, orchestrator) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-o", role = "orchestrator" });
        var (_, byMinted) = await MintAsync(server, Token(orchestrator), new { name = "aw-oc", role = "worker" });

        var controllers = byController.GetProperty("sessionId").GetGuid();
        var minteds = byMinted.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await PatchAsync(server, ReadServer.OrchestratorToken, controllers, "c1"));
        Assert.Equal(HttpStatusCode.Forbidden, await PatchAsync(server, Token(orchestrator), controllers, "c1"));
        Assert.Equal(HttpStatusCode.Forbidden, await PatchAsync(server, ReadServer.OrchestratorToken, minteds, "c2"));
        Assert.Equal(HttpStatusCode.NoContent, await PatchAsync(server, Token(orchestrator), minteds, "c2"));
        Assert.Equal(HttpStatusCode.NoContent, await PatchAsync(server, ReadServer.ControllerToken, minteds, "c3"));

        // A static orchestrator cannot tell itself from the static controller, so rank decides: a
        // controller session is never an orchestrator's to change.
        var (_, controllerRole) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-cc", role = "controller" });
        var controllerRoles = controllerRole.GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteAsync(server, ReadServer.OrchestratorToken, controllerRoles, Ct));
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteAsync(server, ReadServer.WorkerToken, minteds, Ct));
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(server, Token(orchestrator), minteds, Ct));
    }

    [Fact]
    public async Task Revoking_refuses_the_token_and_releases_the_sessions_claims()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-revoke", role = "orchestrator" });
        var id = minted.GetProperty("sessionId").GetGuid();

        await using (var client = await server.ConnectAsync(Token(minted), Ct))
        {
            var (text, refused) = await ReadServer.CallEitherAsync(
                client,
                "work_status",
                new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "in_progress", ["tree"] = Tree() },
                Ct);
            Assert.False(refused, text);
        }

        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));

        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(server, ReadServer.ControllerToken, id, Ct));

        // Refused at the transport, not by a tool: an MCP client reports a 401 as a dead credential.
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(minted)));
        await Assert.ThrowsAnyAsync<Exception>(async () => await server.ConnectAsync(Token(minted), Ct));

        await using var db = fixture.NewContext();
        Assert.NotNull((await db.AgentSessions.SingleAsync(s => s.Id == id, Ct)).RevokedAt);

        var claim = await db.Claims.SingleAsync(c => c.ItemId == world.TaskId, Ct);
        Assert.NotNull(claim.ReleasedAt);
        Assert.Equal("revoked", claim.ReleaseReason);
        Assert.Equal(id, claim.SessionId);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));
        Assert.Equal(id, (await db.Events.SingleAsync(e => e.ItemId == world.TaskId && e.Kind == "claim_revoked", Ct)).SessionId);

        // Revoked is not live: the name can be minted again without replace, on the same row.
        var (again, body) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-revoke", role = "worker" });
        Assert.Equal(HttpStatusCode.Created, again);
        Assert.Equal(id, body.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-expired", role = "worker" });
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(minted)));

        await using (var db = fixture.NewContext())
        {
            var row = await db.AgentSessions.SingleAsync(s => s.Id == minted.GetProperty("sessionId").GetGuid(), Ct);
            row.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(minted)));

        var (mint, _) = await MintAsync(server, Token(minted), new { name = "aw-x", role = "worker" });
        Assert.Equal(HttpStatusCode.Unauthorized, mint);

        // Expired is not live either.
        var (again, _) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-expired", role = "worker" });
        Assert.Equal(HttpStatusCode.Created, again);
    }

    /// <summary>
    /// The expiry is an idle window, not a lifetime: a use slides it to now plus the window, with no
    /// cap, and a use inside the minute after a renewal writes nothing.
    /// </summary>
    [Fact]
    public async Task A_used_token_slides_its_expiry_and_a_second_use_within_the_minute_writes_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-slide", role = "worker" });
        var id = minted.GetProperty("sessionId").GetGuid();
        await SetExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(minted)));

        var renewed = await ExpiryAsync(id);
        Assert.InRange(renewed, DateTimeOffset.UtcNow.AddHours(72).AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(72).AddMinutes(1));

        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(minted)));
        Assert.Equal(renewed, await ExpiryAsync(id));
    }

    [Fact]
    public async Task A_token_slides_by_its_own_window()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-five", role = "worker", ttlHours = 5 });
        var id = minted.GetProperty("sessionId").GetGuid();
        await SetExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(minted)));

        Assert.InRange(
            await ExpiryAsync(id),
            DateTimeOffset.UtcNow.AddHours(5).AddMinutes(-2),
            DateTimeOffset.UtcNow.AddHours(5).AddMinutes(1));
    }

    /// <summary>The renewal and the lookup race on one row: every concurrent use authenticates.</summary>
    [Fact]
    public async Task Concurrent_uses_of_a_token_due_for_renewal_all_authenticate()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-race", role = "worker" });
        var id = minted.GetProperty("sessionId").GetGuid();
        await SetExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(1));

        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => InitializeAsync(server, Token(minted))));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.True(await ExpiryAsync(id) > DateTimeOffset.UtcNow.AddHours(71));
    }

    [Fact]
    public async Task A_revoked_token_presented_again_is_refused_and_not_renewed()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-gone", role = "worker" });
        var id = minted.GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(server, ReadServer.ControllerToken, id, Ct));

        var near = await SetExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(minted)));
        Assert.Equal(near, await ExpiryAsync(id));
    }

    [Fact]
    public async Task An_expired_token_presented_again_is_refused_and_not_renewed()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-lapsed", role = "worker" });
        var id = minted.GetProperty("sessionId").GetGuid();
        var lapsed = await SetExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(minted)));
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(minted)));
        Assert.Equal(lapsed, await ExpiryAsync(id));
    }

    [Fact]
    public async Task A_row_with_no_stored_window_slides_by_the_default()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-old", role = "worker", ttlHours = 5 });
        var id = minted.GetProperty("sessionId").GetGuid();

        await using (var db = fixture.NewContext())
        {
            var row = await db.AgentSessions.SingleAsync(s => s.Id == id, Ct);
            Assert.Equal(5, row.TokenTtlHours);
            row.TokenTtlHours = null;
            row.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(minted)));

        Assert.InRange(
            await ExpiryAsync(id),
            DateTimeOffset.UtcNow.AddHours(SessionStore.DefaultTtlHours).AddMinutes(-2),
            DateTimeOffset.UtcNow.AddHours(SessionStore.DefaultTtlHours).AddMinutes(1));
    }

    [Fact]
    public async Task Static_tokens_renew_no_minted_row()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-still", role = "worker" });
        var id = minted.GetProperty("sessionId").GetGuid();
        var near = await SetExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(1));

        foreach (var token in new[] { ReadServer.WorkerToken, ReadServer.OrchestratorToken, ReadServer.ControllerToken })
        {
            Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, token));
        }

        Assert.Equal(near, await ExpiryAsync(id));
    }

    [Fact]
    public async Task No_token_an_unknown_token_or_a_dashboard_cookie_is_401_on_the_endpoints()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        var body = new { name = "aw-anon", role = "worker" };

        var anonymous = server.NewClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/api/agent-sessions", AppServer.Json(body), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.DeleteAsync($"/api/agent-sessions/{Guid.CreateVersion7()}", Ct)).StatusCode);

        foreach (var token in new[] { "not-a-token", $"{SessionToken.Prefix}{new string('A', 43)}" })
        {
            var (status, _) = await MintAsync(server, token, body);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, token));
        }

        var cookie = await server.SignInAsync(Ct);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await cookie.PostAsync("/api/agent-sessions", AppServer.Json(body), Ct)).StatusCode);

        await AssertNoSessionAsync(world);
    }

    /// <summary>
    /// The guard: the static tokens authenticate, nest and name sessions by header exactly as before
    /// tokens could be minted — no session from the credential, the header still names one.
    /// </summary>
    [Fact]
    public async Task Static_tokens_behave_as_before()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        foreach (var token in new[] { ReadServer.WorkerToken, ReadServer.OrchestratorToken, ReadServer.ControllerToken })
        {
            Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, token));
        }

        await using var worker = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        await using var orchestrator = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);
        await using var controller = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var (w, o, c) = (await ToolsAsync(worker), await ToolsAsync(orchestrator), await ToolsAsync(controller));
        Assert.DoesNotContain("work_status", w);
        Assert.Contains("work_status", o);
        Assert.Subset(c.ToHashSet(), o.ToHashSet());
        Assert.Subset(o.ToHashSet(), w.ToHashSet());

        await using var unnamed = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        await VerifyAsync(unnamed, "unnamed");
        await AssertNoSessionAsync(world);

        await using var named = await server.ConnectAsync(
            ReadServer.WorkerToken, Ct, new Dictionary<string, string> { [CallerSession.AgentHeader] = "aw-header" });
        await ReadServer.CallAsync(named, "work_brief", null, Ct);

        await using var db = fixture.NewContext();
        var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("aw-header", row.Name);
        Assert.Null(row.TokenHash);
        Assert.Null(row.GrantedRole);
    }

    /// <summary>
    /// The Down a rollback would run, on a database of its own: a session holding a minted token goes
    /// down to a plain doc 11 row and comes back up with no token, which is a session that can no
    /// longer authenticate rather than one that silently can.
    /// </summary>
    [Fact]
    public async Task The_tokens_migration_goes_down_and_up_again()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }
            .ConnectionString;

        try
        {
            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
            }

            var workspace = Guid.CreateVersion7();
            var session = Guid.CreateVersion7();

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO agent_session (id, workspace_id, name, role, token_hash, granted_role, token_expires_at)
                    VALUES (@session, @workspace, 'aw-m10', 'worker', @hash, 'worker', now() + interval '72 hours');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}", session, hash = SessionToken.Hash(SessionToken.New()) });
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeTokens, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                var columns = (await connection.QueryAsync<string>(
                    """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_name = 'agent_session'
                      AND column_name IN ('token_hash', 'granted_role', 'token_expires_at', 'revoked_at', 'minted_by_session_id');
                    """)).ToList();
                Assert.Empty(columns);

                Assert.Equal("aw-m10", await connection.ExecuteScalarAsync<string>(
                    "SELECT name FROM agent_session WHERE id = @session;", new { session }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));

                var row = await context.AgentSessions.SingleAsync(s => s.Id == session, Ct);
                Assert.Null(row.TokenHash);
                Assert.Null(row.GrantedRole);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>
    /// Down drops only the stored window: the token survives it, and once back up the row is one
    /// minted before the window was stored, which still authenticates and slides by the default.
    /// </summary>
    [Fact]
    public async Task The_sliding_expiry_migration_goes_down_and_up_again()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }
            .ConnectionString;

        try
        {
            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
            }

            var workspace = Guid.CreateVersion7();
            var session = Guid.CreateVersion7();
            var token = SessionToken.New();

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO agent_session (id, workspace_id, name, role, token_hash, granted_role, token_expires_at, token_ttl_hours)
                    VALUES (@session, @workspace, 'aw-m12', 'worker', @hash, 'worker', now() + interval '1 minute', 5);
                    """,
                    new { workspace, slug = $"mig-{workspace:n}", session, hash = SessionToken.Hash(token) });
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeSlidingExpiry, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_name = 'agent_session' AND column_name = 'token_ttl_hours';
                    """));

                Assert.Equal(SessionToken.Hash(token), await connection.ExecuteScalarAsync<string>(
                    "SELECT token_hash FROM agent_session WHERE id = @session;", new { session }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
                Assert.Null((await context.AgentSessions.SingleAsync(s => s.Id == session, Ct)).TokenTtlHours);
            }

            await using (var dataSource = NpgsqlDataSource.Create(connectionString))
            {
                Assert.Equal(session, (await new SessionStore(dataSource).AuthenticateAsync(token, Ct))?.SessionId);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                Assert.InRange(
                    (await context.AgentSessions.SingleAsync(s => s.Id == session, Ct)).TokenExpiresAt!.Value,
                    DateTimeOffset.UtcNow.AddHours(SessionStore.DefaultTtlHours).AddMinutes(-2),
                    DateTimeOffset.UtcNow.AddHours(SessionStore.DefaultTtlHours).AddMinutes(1));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>Returns the value as stored, so a later read compares equal to it if nothing wrote.</summary>
    private async Task<DateTimeOffset> SetExpiryAsync(Guid id, DateTimeOffset expiresAt)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<DateTimeOffset>(
            "UPDATE agent_session SET token_expires_at = @expiresAt WHERE id = @id RETURNING token_expires_at;",
            new { id, expiresAt });
    }

    private async Task<DateTimeOffset> ExpiryAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<DateTimeOffset>(
            "SELECT token_expires_at FROM agent_session WHERE id = @id;", new { id });
    }

    private Task<AppServer> StartAsync(WriteWorld world)
        => AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> MintAsync(AppServer server, string token, object body)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/agent-sessions", AppServer.Json(body), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<HttpStatusCode> PatchAsync(AppServer server, string token, Guid id, string claudeSessionId)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsync($"/api/agent-sessions/{id}", AppServer.Json(new { claudeSessionId }), Ct);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> DeleteAsync(AppServer server, string token, Guid id, CancellationToken cancellationToken)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (await client.DeleteAsync($"/api/agent-sessions/{id}", cancellationToken)).StatusCode;
    }

    /// <summary>Straight at <c>/mcp</c>: the status the transport answers before any tool runs.</summary>
    private static async Task<HttpStatusCode> InitializeAsync(AppServer server, string token)
    {
        var request = AppServer.McpInitialize();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (await server.NewClient().SendAsync(request, Ct)).StatusCode;
    }

    private static Task<string> VerifyAsync(McpClient client, string label)
        => ReadServer.CallAsync(
            client,
            "work_verify",
            new Dictionary<string, object?>
            {
                ["ref"] = "1.1",
                ["result_line"] = $"Passed! Failed: 0, Passed: 1 ({label})",
            },
            Ct);

    private static async Task<List<string>> ToolsAsync(McpClient client)
        => (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order().ToList();

    private static string Token(JsonElement minted) => minted.GetProperty("token").GetString()!;

    private async Task AssertNoSessionAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        Assert.False(await db.AgentSessions.AnyAsync(s => s.WorkspaceId == world.WorkspaceId, Ct));
    }

    /// <summary>A path lock is server-wide, and every test in the collection shares one server.</summary>
    private static string Tree() => $"p:/wt-{Guid.NewGuid():n}"[..18];
}

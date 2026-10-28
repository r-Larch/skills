using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkState.Api.App;
using WorkState.Core.Writing;
using WorkState.Mcp;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 11 — who may have a session name. A launcher takes over a launcher-less row only by proving it
/// is the same Claude session; any other row an operator frees with release, and the next mint
/// creates the session fresh.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SessionNameTests(PostgresFixture fixture)
{
    private const string Foreman = "foreman/hand";

    [Fact]
    public async Task A_launcher_takes_over_a_launcherless_header_row_with_the_same_claude_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "mw-workflow", "claude-a", verify: true);

        var before = await RowAsync(world, "mw-workflow");
        Assert.Null(before.LaunchedBy);

        var (status, body) = await MintAsync(server, new
        {
            name = "mw-workflow", role = "worker", launchedBy = Foreman, claudeSessionId = " claude-a ", replace = true,
        });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(before.Id, body.GetProperty("sessionId").GetGuid());

        var after = await RowAsync(world, "mw-workflow");
        Assert.Equal(Foreman, after.LaunchedBy);
        Assert.Equal("claude-a", after.ClaudeSessionId);

        await using var db = fixture.NewContext();
        Assert.Single(await db.AgentSessions.Where(s => s.WorkspaceId == world.WorkspaceId).ToListAsync(Ct));
        Assert.Equal(before.Id, (await db.Events.SingleAsync(e => e.ItemId == world.TaskId && e.Kind == "verified", Ct)).SessionId);

        var taken = await db.Events.SingleAsync(
            e => e.WorkspaceId == world.WorkspaceId && e.Kind == "session_launcher_taken_over", Ct);
        Assert.Equal(before.Id, taken.SessionId);
        Assert.Contains(Foreman, taken.Payload, StringComparison.Ordinal);
        Assert.Contains("claude-a", taken.Summary, StringComparison.Ordinal);

        // From here the launcher's own rule holds: the same launcher mints again, nobody else does.
        var (again, _) = await MintAsync(server, new { name = "mw-workflow", role = "worker", launchedBy = Foreman, replace = true });
        Assert.Equal(HttpStatusCode.Created, again);
    }

    [Fact]
    public async Task A_takeover_replaces_the_launcherless_rows_token()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, first) = await MintAsync(server, new { name = "aw-token", role = "worker", claudeSessionId = "claude-t" });
        var id = first.GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(first)));

        var (status, second) = await MintAsync(server, new
        {
            name = "aw-token", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-t", replace = true,
        });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(id, second.GetProperty("sessionId").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(first)));
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(second)));
        Assert.Equal(Foreman, (await RowAsync(world, "aw-token")).LaunchedBy);
    }

    [Fact]
    public async Task A_launcherless_row_without_a_recorded_claude_session_is_not_taken_over()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "aw-noid", claudeSessionId: null);

        await AssertRefusedAsync(server, world, "aw-noid", null,
            new { name = "aw-noid", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-a", replace = true });
    }

    [Fact]
    public async Task A_launcherless_row_with_a_different_claude_session_is_not_taken_over()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "aw-other", "claude-a");

        await AssertRefusedAsync(server, world, "aw-other", null,
            new { name = "aw-other", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-b", replace = true });
        await AssertRefusedAsync(server, world, "aw-other", null,
            new { name = "aw-other", role = "worker", launchedBy = Foreman, claudeSessionId = "CLAUDE-A", replace = true });
        await AssertRefusedAsync(server, world, "aw-other", null,
            new { name = "aw-other", role = "worker", launchedBy = Foreman, replace = true });
    }

    [Fact]
    public async Task A_takeover_needs_replace()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "aw-noreplace", "claude-a");

        await AssertRefusedAsync(server, world, "aw-noreplace", null,
            new { name = "aw-noreplace", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-a" });
        await AssertRefusedAsync(server, world, "aw-noreplace", null,
            new { name = "aw-noreplace", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-a", replace = false });
    }

    [Fact]
    public async Task Two_launchers_never_share_a_name_even_with_the_same_claude_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (minted, _) = await MintAsync(server, new
        {
            name = "aw-held", role = "worker", launchedBy = "skills-5d", claudeSessionId = "claude-a",
        });
        Assert.Equal(HttpStatusCode.Created, minted);

        await AssertRefusedAsync(server, world, "aw-held", "skills-5d",
            new { name = "aw-held", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-a", replace = true });
    }

    [Fact]
    public async Task A_mint_without_a_launcher_never_takes_a_launchers_row()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (minted, _) = await MintAsync(server, new
        {
            name = "aw-mine", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-a",
        });
        Assert.Equal(HttpStatusCode.Created, minted);

        await AssertRefusedAsync(server, world, "aw-mine", Foreman,
            new { name = "aw-mine", role = "worker", claudeSessionId = "claude-a", replace = true });
    }

    [Fact]
    public async Task A_released_name_mints_fresh_and_the_old_row_keeps_its_history()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "mw-workflow", claudeSessionId: null, verify: true);
        var old = await RowAsync(world, "mw-workflow");

        var (refused, conflict) = await MintAsync(server, new
        {
            name = "mw-workflow", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-new", replace = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains(SessionStore.ReleasePath(old.Id), Error(conflict), StringComparison.Ordinal);

        var (released, body) = await ReleaseAsync(Bearer(server, ReadServer.ControllerToken), old.Id, header: false);
        Assert.Equal(HttpStatusCode.OK, released);
        Assert.Equal("mw-workflow", body.GetProperty("name").GetString());
        var tombstone = SessionStore.TombstoneName("mw-workflow", old.Id);
        Assert.Equal(tombstone, body.GetProperty("releasedAs").GetString());

        var (minted, fresh) = await MintAsync(server, new
        {
            name = "mw-workflow", role = "worker", launchedBy = Foreman, claudeSessionId = "claude-new",
        });
        Assert.Equal(HttpStatusCode.Created, minted);
        Assert.NotEqual(old.Id, fresh.GetProperty("sessionId").GetGuid());

        await using var db = fixture.NewContext();
        var kept = await db.AgentSessions.SingleAsync(s => s.Id == old.Id, Ct);
        Assert.Equal(tombstone, kept.Name);
        Assert.NotNull(kept.RevokedAt);
        Assert.Equal(old.Id, (await db.Events.SingleAsync(e => e.ItemId == world.TaskId && e.Kind == "verified", Ct)).SessionId);

        var journal = await db.Events.SingleAsync(e => e.WorkspaceId == world.WorkspaceId && e.Kind == "session_name_released", Ct);
        Assert.Equal(old.Id, journal.SessionId);
        Assert.Equal("controller", journal.Actor);
        Assert.Contains(tombstone, journal.Summary, StringComparison.Ordinal);

        var (twice, again) = await ReleaseAsync(Bearer(server, ReadServer.ControllerToken), old.Id, header: false);
        Assert.Equal(HttpStatusCode.Conflict, twice);
        Assert.Contains("already released", Error(again), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_revokes_a_minted_token()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await MintAsync(server, new { name = "aw-rel", role = "worker" });
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(server, Token(minted)));

        var (released, _) = await ReleaseAsync(
            Bearer(server, ReadServer.ControllerToken), minted.GetProperty("sessionId").GetGuid(), header: false);

        Assert.Equal(HttpStatusCode.OK, released);
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(server, Token(minted)));
    }

    [Fact]
    public async Task A_session_holding_live_claims_is_not_released()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        await using (var client = await server.ConnectAsync(
            ReadServer.OrchestratorToken, Ct, new Dictionary<string, string> { [CallerSession.AgentHeader] = "aw-busy" }))
        {
            var (text, refused) = await ReadServer.CallEitherAsync(
                client,
                "work_status",
                new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "in_progress", ["tree"] = Tree() },
                Ct);
            Assert.False(refused, text);
        }

        var busy = await RowAsync(world, "aw-busy");
        var (status, body) = await ReleaseAsync(Bearer(server, ReadServer.ControllerToken), busy.Id, header: false);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("1.1", Error(body), StringComparison.Ordinal);
        Assert.Contains($"DELETE /api/agent-sessions/{busy.Id}", Error(body), StringComparison.Ordinal);
        Assert.Equal("aw-busy", (await RowAsync(world, "aw-busy")).Name);

        // The existing way to let go of the claims, then the release goes through.
        var revoke = await Bearer(server, ReadServer.ControllerToken).DeleteAsync($"/api/agent-sessions/{busy.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReleaseAsync(Bearer(server, ReadServer.ControllerToken), busy.Id, header: false)).Status);
    }

    [Fact]
    public async Task Only_a_controller_or_the_dashboard_operator_releases_a_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "aw-op", claudeSessionId: null);
        var id = (await RowAsync(world, "aw-op")).Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await ReleaseAsync(Bearer(server, ReadServer.OrchestratorToken), id, header: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReleaseAsync(Bearer(server, ReadServer.WorkerToken), id, header: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReleaseAsync(server.NewClient(), id, header: false)).Status);

        // The dashboard's door is cookie only, and only with the write header.
        var cookie = await server.SignInAsync(Ct);
        var noHeader = await ReleaseAsync(cookie, id, header: false, path: $"/api/sessions/{id}/release");
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Contains(ProjectEndpoints.WriteHeader, Error(noHeader.Body), StringComparison.Ordinal);
        Assert.True(
            (await ReleaseAsync(Bearer(server, ReadServer.ControllerToken), id, header: true, path: $"/api/sessions/{id}/release")).Status
                is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal("aw-op", (await RowAsync(world, "aw-op")).Name);

        var (released, _) = await ReleaseAsync(cookie, id, header: true, path: $"/api/sessions/{id}/release");
        Assert.Equal(HttpStatusCode.OK, released);

        await using var db = fixture.NewContext();
        var journal = await db.Events.SingleAsync(e => e.WorkspaceId == world.WorkspaceId && e.Kind == "session_name_released", Ct);
        Assert.Equal(Core.Domain.ActorRole.Human, journal.Role);

        Assert.Equal(HttpStatusCode.NotFound, (await ReleaseAsync(cookie, Guid.CreateVersion7(), header: true,
            path: $"/api/sessions/{Guid.CreateVersion7()}/release")).Status);
    }

    [Fact]
    public async Task The_sessions_list_says_whether_each_token_is_live()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        await HeaderSessionAsync(server, "aw-plain", claudeSessionId: null);
        await MintAsync(server, new { name = "aw-live", role = "worker" });
        var (_, gone) = await MintAsync(server, new { name = "aw-gone", role = "worker" });
        await Bearer(server, ReadServer.ControllerToken).DeleteAsync($"/api/agent-sessions/{gone.GetProperty("sessionId").GetGuid()}", Ct);

        var cookie = await server.SignInAsync(Ct);
        var text = await cookie.GetStringAsync("/api/v1/sessions", Ct);
        var tokens = JsonDocument.Parse(text).RootElement.GetProperty("sessions").EnumerateArray()
            .ToDictionary(s => s.GetProperty("session").GetProperty("name").GetString()!, s => s.GetProperty("token").GetString());

        Assert.Equal("none", tokens["aw-plain"]);
        Assert.Equal("live", tokens["aw-live"]);
        Assert.Equal("revoked", tokens["aw-gone"]);
    }

    private async Task AssertRefusedAsync(AppServer server, WriteWorld world, string name, string? launcher, object body)
    {
        var before = await RowAsync(world, name);
        var (status, conflict) = await MintAsync(server, body);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("two launchers", Error(conflict), StringComparison.Ordinal);
        Assert.Contains(SessionStore.ReleasePath(before.Id), Error(conflict), StringComparison.Ordinal);

        var after = await RowAsync(world, name);
        Assert.Equal(launcher, after.LaunchedBy);
        Assert.Equal(before.TokenHash, after.TokenHash);

        await using var db = fixture.NewContext();
        Assert.False(await db.Events.AnyAsync(
            e => e.WorkspaceId == world.WorkspaceId && e.Kind == "session_launcher_taken_over", Ct));
    }

    /// <summary>A hand-started session as Foreman met it: named by header, no launcher.</summary>
    private static async Task HeaderSessionAsync(AppServer server, string name, string? claudeSessionId, bool verify = false)
    {
        var headers = new Dictionary<string, string> { [CallerSession.AgentHeader] = name };
        if (claudeSessionId is not null)
        {
            headers[CallerSession.SessionHeader] = claudeSessionId;
        }

        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct, headers);
        await ReadServer.CallAsync(client, "work_brief", null, Ct);

        if (verify)
        {
            await ReadServer.CallAsync(
                client,
                "work_verify",
                new Dictionary<string, object?> { ["ref"] = "1.1", ["result_line"] = "Passed! Failed: 0, Passed: 1" },
                Ct);
        }
    }

    private async Task<Core.Domain.AgentSession> RowAsync(WriteWorld world, string name)
    {
        await using var db = fixture.NewContext();
        return await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId && s.Name == name, Ct);
    }

    private Task<AppServer> StartAsync(WriteWorld world)
        => AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

    private static HttpClient Bearer(AppServer server, string token)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> MintAsync(AppServer server, object body)
    {
        var response = await Bearer(server, ReadServer.ControllerToken).PostAsync("/api/agent-sessions", AppServer.Json(body), Ct);
        return (response.StatusCode, await BodyAsync(response));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReleaseAsync(
        HttpClient client, Guid id, bool header, string? path = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path ?? $"/api/agent-sessions/{id}/release");
        if (header)
        {
            request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        }

        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await BodyAsync(response));
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpStatusCode> InitializeAsync(AppServer server, string token)
    {
        var request = AppServer.McpInitialize();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await server.NewClient().SendAsync(request, Ct)).StatusCode;
    }

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    private static string Token(JsonElement minted) => minted.GetProperty("token").GetString()!;

    private static string Tree() => $"p:/wt-{Guid.NewGuid():n}"[..18];
}

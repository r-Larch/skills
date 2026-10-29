using System.Net;
using System.Text.Json;
using Dapper;
using WorkState.Api.App;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m62, doc 10 <c>/sessions</c>. Sessions belong to no project, so project access does not cover them: a member sees and
/// changes only the sessions minted under their own user; an admin of the tenant, a system administrator, or a
/// credential with no user sees every one. Anyone else's session is not found, never forbidden.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SessionsPerUserTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SessionsPerUser_a_member_sees_only_their_own_sessions()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CredentialsAsync(world.User1);
        await world.CredentialsAsync(world.User2);

        var mine = await SessionIdsAsync(world, world.User1Id);
        Assert.NotEmpty(mine);
        Assert.NotEmpty(await SessionIdsAsync(world, world.User2Id));

        foreach (var credential in u1)
        {
            Assert.Equal(mine.Order(), (await ListedAsync(credential.Http, "/api/v1/sessions")).Order());
        }
    }

    [Fact]
    public async Task SessionsPerUser_a_tenant_admin_sees_both_users_sessions()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await world.CredentialsAsync(world.User1);
        var u2 = await world.CredentialsAsync(world.User2);
        await AdminAsync(world, world.User2Id);

        var listed = await ListedAsync(u2[1].Http, "/api/v1/sessions");

        Assert.Superset(new HashSet<Guid>([.. await SessionIdsAsync(world, world.User1Id), .. await SessionIdsAsync(world, world.User2Id)]), listed.ToHashSet());
    }

    [Fact]
    public async Task SessionsPerUser_the_static_token_sees_every_session()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await world.CredentialsAsync(world.User1);
        await world.CredentialsAsync(world.User2);

        var listed = await ListedAsync(world.Static.Http, "/api/v1/sessions");

        Assert.Superset(new HashSet<Guid>([.. await SessionIdsAsync(world, world.User1Id), .. await SessionIdsAsync(world, world.User2Id)]), listed.ToHashSet());
    }

    [Fact]
    public async Task SessionsPerUser_a_member_releasing_or_revoking_another_users_session_is_not_found_and_changes_nothing()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CredentialsAsync(world.User1);
        var u2 = await world.CredentialsAsync(world.User2);
        var theirs = (await SessionIdsAsync(world, world.User2Id)).Single();
        var before = await SessionStateAsync(world, theirs);

        foreach (var credential in u1.Skip(1))
        {
            var revoke = await credential.Http.DeleteAsync($"/api/agent-sessions/{theirs}", Ct);
            Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await credential.Http.PostAsync($"/api/agent-sessions/{theirs}/release", null, Ct)).StatusCode);
        }

        // The same body as for a session that does not exist, so the answer says nothing of it.
        var missing = Guid.CreateVersion7();
        Assert.Equal(
            (await BodyAsync(await u1[1].Http.DeleteAsync($"/api/agent-sessions/{missing}", Ct))).Replace(missing.ToString(), "<id>"),
            (await BodyAsync(await u1[1].Http.DeleteAsync($"/api/agent-sessions/{theirs}", Ct))).Replace(theirs.ToString(), "<id>"));

        // The dashboard's door: a member's cookie releases only their own.
        Assert.Equal(HttpStatusCode.NotFound, await CookieReleaseAsync(u1[0].Http, theirs));
        Assert.Equal(before, await SessionStateAsync(world, theirs));

        // An admin of the tenant releases any stuck name in it.
        await AdminAsync(world, world.User1Id);
        Assert.Equal(HttpStatusCode.OK, await CookieReleaseAsync(u1[0].Http, theirs));
        Assert.NotEqual(before, await SessionStateAsync(world, theirs));

        // And a member still releases their own.
        await TenantWorld.MintAsync(u2[1].Http, "u2-second", "worker");
        Assert.Equal(HttpStatusCode.OK, await CookieReleaseAsync(u2[0].Http, (await SessionIdsAsync(world, world.User2Id)).Single(s => s != theirs)));
    }

    private static Task<string> BodyAsync(HttpResponseMessage response) => response.Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task SessionsPerUser_a_claude_session_id_finds_the_users_own_session_and_not_for_another_member()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CredentialsAsync(world.User1);
        var u2 = await world.CredentialsAsync(world.User2);
        var claude = $"claude-{Guid.CreateVersion7():n}";

        var minted = await u1[1].Http.PostAsync(
            "/api/agent-sessions", AppServer.Json(new { name = "foreman-u1", role = "worker", claudeSessionId = claude }), Ct);
        Assert.Equal(HttpStatusCode.Created, minted.StatusCode);
        var id = JsonDocument.Parse(await minted.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal([id], await ListedAsync(u1[1].Http, $"/api/v1/sessions?claudeSessionId={claude}"));
        Assert.Empty(await ListedAsync(u2[1].Http, $"/api/v1/sessions?claudeSessionId={claude}"));
    }

    private static async Task<HttpStatusCode> CookieReleaseAsync(HttpClient cookie, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{id}/release");
        request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        return (await cookie.SendAsync(request, Ct)).StatusCode;
    }

    private static async Task<List<Guid>> ListedAsync(HttpClient http, string path)
    {
        var response = await http.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} → {(int)response.StatusCode}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("sessions").EnumerateArray()
            .Select(s => s.GetProperty("session").GetProperty("id").GetGuid())];
    }

    private static async Task<List<Guid>> SessionIdsAsync(ProjectWorld world, Guid user)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<Guid>(
            "SELECT id FROM agent_session WHERE workspace_id = @workspace AND user_id = @user;",
            new { workspace = world.WorkspaceId, user })).AsList();
    }

    private static async Task<string> SessionStateAsync(ProjectWorld world, Guid id)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleAsync<string>(
            "SELECT name || ' ' || coalesce(revoked_at::text, 'live') FROM agent_session WHERE id = @id;", new { id });
    }

    private static async Task AdminAsync(ProjectWorld world, Guid user)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            "UPDATE tenant_membership SET role = 'admin' WHERE user_id = @user AND workspace_id = @workspace;",
            new { user, workspace = world.WorkspaceId });
    }
}

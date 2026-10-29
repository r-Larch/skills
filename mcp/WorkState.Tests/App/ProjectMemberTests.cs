using System.Net;
using System.Text.Json;
using Dapper;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 02 §12b: who may work in a project. A project is its creator's until shared; a static token, which has no
/// user, gives it every member of the workspace. The sharing API is the dashboard's (doc 10, "Outside v1"): a
/// member of the project or an admin of its workspace may share it, and everyone else — another tenant's user
/// included (C63) — gets the 404 an unknown id gets.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectMemberTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ProjectMember_work_create_with_an_oauth_token_makes_its_user_the_only_member()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var token = await world.OAuthAsync(await server.SignInAsync(Ct), $"{server.BaseAddress}/mcp", "mcp offline_access");

        await using (var mcp = await world.McpAsync(token))
        {
            var created = await ReadServer.CallEitherAsync(mcp, "work_create", Args(("items", new[]
            {
                new NewItemArg("project", "the operator's own", Ref: "pm-oauth"),
            })), Ct);
            Assert.False(created.Refused, created.Text);
        }

        var project = await ProjectIdAsync(world.A.WorkspaceId, "pm-oauth");
        Assert.Equal([AppServer.Username], await MemberNamesAsync(project));
        Assert.True(await TenantMemberCountAsync(world.A.WorkspaceId) > 1, "tenant A has one member, so this proves nothing");
    }

    [Fact]
    public async Task ProjectMember_work_create_with_a_static_token_makes_every_tenant_member_a_member()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;

        await using (var mcp = await world.McpAsync(ReadServer.ControllerToken))
        {
            var created = await ReadServer.CallEitherAsync(mcp, "work_create", Args(("items", new[]
            {
                new NewItemArg("project", "the install's", Ref: "pm-static"),
            })), Ct);
            Assert.False(created.Refused, created.Text);
        }

        var project = await ProjectIdAsync(world.A.WorkspaceId, "pm-static");
        Assert.Equal(
            (await TenantMemberNamesAsync(world.A.WorkspaceId)).Order(),
            (await MemberNamesAsync(project)).Order());
        Assert.Equal(
            [AppServer.Username, world.UserA, world.UserAB],
            (await MemberNamesAsync(project)).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProjectMember_a_member_adds_one_adds_all_and_removes_one_through_the_api()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var project = await PrivateProjectAsync(world, world.UserA, "pm-share");
        var userAB = await world.UserIdAsync(world.UserAB);
        var a = await world.CookieAsync(world.UserA);

        var before = await AdminApi.JsonAsync(await AdminApi.SendAsync(a, HttpMethod.Get, Members(project), header: false), HttpStatusCode.OK);
        Assert.Equal([world.UserA], Names(before, "members"));
        Assert.Equal([AppServer.Username, world.UserAB], Names(before, "candidates").Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(before.GetProperty("everyone").GetBoolean());

        var one = await AdminApi.JsonAsync(
            await AdminApi.SendAsync(a, HttpMethod.Post, Members(project), new { userId = userAB }), HttpStatusCode.OK);
        Assert.Equal([world.UserA, world.UserAB], Names(one, "members").Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(world.UserA, one.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == world.UserAB).GetProperty("addedBy").GetString());

        var all = await AdminApi.JsonAsync(
            await AdminApi.SendAsync(a, HttpMethod.Post, Members(project), new { all = true }), HttpStatusCode.OK);
        Assert.True(all.GetProperty("everyone").GetBoolean());
        Assert.Empty(Names(all, "candidates"));
        Assert.Equal((await TenantMemberNamesAsync(world.A.WorkspaceId)).Order(), (await MemberNamesAsync(project)).Order());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await AdminApi.SendAsync(a, HttpMethod.Delete, $"{Members(project)}/{userAB}")).StatusCode);
        Assert.DoesNotContain(world.UserAB, await MemberNamesAsync(project));

        // The journal says who shared it with whom.
        Assert.Equal(["member_added", "member_added", "member_removed"], await EventKindsAsync(project));

        // A write without the dashboard's header is refused, and nothing changes.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await AdminApi.SendAsync(a, HttpMethod.Post, Members(project), new { userId = userAB }, header: false)).StatusCode);
        Assert.DoesNotContain(world.UserAB, await MemberNamesAsync(project));
    }

    [Fact]
    public async Task ProjectMember_the_last_member_of_a_project_cannot_be_removed()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var project = await PrivateProjectAsync(world, world.UserA, "pm-last");
        var userA = await world.UserIdAsync(world.UserA);
        var a = await world.CookieAsync(world.UserA);

        var refused = await AdminApi.SendAsync(a, HttpMethod.Delete, $"{Members(project)}/{userA}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("last member", await AdminApi.ErrorAsync(refused), StringComparison.Ordinal);
        Assert.Equal([world.UserA], await MemberNamesAsync(project));
    }

    [Fact]
    public async Task ProjectMember_a_non_member_and_another_tenants_user_are_refused_like_an_unknown_id()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var project = await PrivateProjectAsync(world, world.UserA, "pm-private");
        var inB = await PrivateProjectAsync(world, world.UserB, "pm-of-b", world.B);
        var userA = await world.UserIdAsync(world.UserA);
        var userB = await world.UserIdAsync(world.UserB);
        var unknown = Guid.CreateVersion7();

        // UserAB is in tenant A (their cookie lands there) but not in the project; UserB is in tenant B only.
        var a = await world.CookieAsync(world.UserA);
        var nonMember = await world.CookieAsync(world.UserAB);
        var otherTenant = await world.CookieAsync(world.UserB);

        var expected = await RefusalsAsync(a, unknown, userA);
        Assert.All(expected, r => Assert.Equal(HttpStatusCode.NotFound, r.Status));

        foreach (var (caller, id) in new[] { (nonMember, project), (otherTenant, project), (a, inB) })
        {
            var seen = await RefusalsAsync(caller, id, userA);
            Assert.Equal(expected.Select(r => r.Status), seen.Select(r => r.Status));
            Assert.Equal(expected.Select(r => r.Error), seen.Select(r => r.Error.Replace(id.ToString(), unknown.ToString())));
        }

        // A user of tenant B cannot be added to a project in A, by the project's own member either.
        var added = await AdminApi.SendAsync(a, HttpMethod.Post, Members(project), new { userId = userB });
        Assert.Equal(HttpStatusCode.NotFound, added.StatusCode);
        Assert.Contains("No member of this workspace", await AdminApi.ErrorAsync(added), StringComparison.Ordinal);

        Assert.Equal([world.UserA], await MemberNamesAsync(project));
        Assert.Equal([world.UserB], await MemberNamesAsync(inB));

        // The workspace's admin may share a project they are not in.
        var admin = await server.SignInAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await AdminApi.SendAsync(admin, HttpMethod.Get, Members(project), header: false)).StatusCode);
    }

    [Fact]
    public async Task ProjectMember_removing_a_tenant_membership_drops_that_tenants_project_memberships_and_disabling_keeps_them()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var shared = await PrivateProjectAsync(world, world.UserA, "pm-shared");
        var sole = await PrivateProjectAsync(world, world.UserAB, "pm-sole");
        var inB = await PrivateProjectAsync(world, world.UserAB, "pm-ab-in-b", world.B);
        var userA = await world.UserIdAsync(world.UserA);
        var userAB = await world.UserIdAsync(world.UserAB);
        var admin = await server.SignInAsync(Ct);

        var a = await world.CookieAsync(world.UserA);
        await AdminApi.JsonAsync(await AdminApi.SendAsync(a, HttpMethod.Post, Members(shared), new { userId = userAB }), HttpStatusCode.OK);

        // Disabling is not leaving: the user may be enabled again, and is still a member of both.
        await AdminApi.JsonAsync(await AdminApi.SendAsync(admin, HttpMethod.Post, $"/api/admin/users/{userAB}/disable"), HttpStatusCode.OK);
        Assert.Contains(world.UserAB, await MemberNamesAsync(shared));
        Assert.Contains(world.UserAB, await MemberNamesAsync(sole));
        await AdminApi.JsonAsync(await AdminApi.SendAsync(admin, HttpMethod.Post, $"/api/admin/users/{userAB}/enable"), HttpStatusCode.OK);

        // The last member of pm-sole cannot leave the workspace until someone else is in it.
        var tenantMember = $"/api/admin/tenants/{world.A.WorkspaceId}/members/{userAB}";
        var refused = await AdminApi.SendAsync(admin, HttpMethod.Delete, tenantMember);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("pm-sole", await AdminApi.ErrorAsync(refused), StringComparison.Ordinal);
        Assert.Contains(world.UserAB, await MemberNamesAsync(shared));

        await AdminApi.JsonAsync(await AdminApi.SendAsync(admin, HttpMethod.Post, Members(sole), new { userId = userA }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AdminApi.SendAsync(admin, HttpMethod.Delete, tenantMember)).StatusCode);

        Assert.Equal([world.UserA], await MemberNamesAsync(shared));
        Assert.Equal([world.UserA], await MemberNamesAsync(sole));
        Assert.Equal([world.UserAB], await MemberNamesAsync(inB));
    }

    private static string Members(Guid project) => $"/api/projects/{project}/members";

    /// <summary>The three routes' answers to <paramref name="caller"/> for <paramref name="project"/>, in order.</summary>
    private static async Task<List<(HttpStatusCode Status, string Error)>> RefusalsAsync(HttpClient caller, Guid project, Guid user)
    {
        List<(HttpStatusCode, string)> seen = [];
        foreach (var response in new[]
                 {
                     await AdminApi.SendAsync(caller, HttpMethod.Get, Members(project), header: false),
                     await AdminApi.SendAsync(caller, HttpMethod.Post, Members(project), new { all = true }),
                     await AdminApi.SendAsync(caller, HttpMethod.Delete, $"{Members(project)}/{user}"),
                 })
        {
            seen.Add((response.StatusCode, await AdminApi.ErrorAsync(response)));
        }

        return seen;
    }

    /// <summary>A project created as <paramref name="user"/>'s own, through the writer as an OAuth caller would.</summary>
    private async Task<Guid> PrivateProjectAsync(TenantWorld world, string user, string reference, WriteWorld? tenant = null)
    {
        tenant ??= world.A;
        var actor = WriteSeed.Orchestrator with { UserId = await world.UserIdAsync(user) };
        var created = await tenant.Writer.CreateAsync(tenant.WorkspaceId, actor, [new NewItemArg("project", reference, Ref: reference)], Ct);
        Assert.False(created.Refused, created.Text);

        var project = await ProjectIdAsync(tenant.WorkspaceId, reference);
        Assert.Equal([user], await MemberNamesAsync(project));
        return project;
    }

    private async Task<Guid> ProjectIdAsync(Guid workspace, string reference)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleAsync<Guid>(
            "SELECT id FROM item WHERE workspace_id = @workspace AND kind = 'project' AND ref = @reference;",
            new { workspace, reference });
    }

    private async Task<List<string>> MemberNamesAsync(Guid project)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<string>(
            """
            SELECT u.name FROM project_member pm JOIN app_user u ON u.id = pm.user_id
            WHERE pm.project_item_id = @project ORDER BY lower(u.name);
            """,
            new { project })).AsList();
    }

    private async Task<List<string>> TenantMemberNamesAsync(Guid workspace)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<string>(
            "SELECT u.name FROM tenant_membership m JOIN app_user u ON u.id = m.user_id WHERE m.workspace_id = @workspace;",
            new { workspace })).AsList();
    }

    private async Task<int> TenantMemberCountAsync(Guid workspace) => (await TenantMemberNamesAsync(workspace)).Count;

    private async Task<List<string>> EventKindsAsync(Guid project)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<string>(
            "SELECT kind FROM event WHERE item_id = @project AND kind LIKE 'member_%' ORDER BY seq;",
            new { project })).AsList();
    }

    private static List<string> Names(JsonElement body, string property)
        => [.. body.GetProperty(property).EnumerateArray().Select(m => m.GetProperty("name").GetString()!)];

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);
}

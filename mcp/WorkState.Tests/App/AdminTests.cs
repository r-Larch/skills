using System.Net;
using Dapper;
using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.AdminApi;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The admin API's rights (C63): a tenant admin acts in their own workspace and nowhere else, a member reaches no
/// admin route at all, a system administrator reaches every one, and a workspace always keeps one admin.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AdminTests(PostgresFixture fixture)
{
    private const string NewPassword = "admin-test-password-4e0a";

    [Fact]
    public async Task A_tenant_Admin_of_A_can_do_nothing_to_B()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var userA = await world.UserIdAsync(world.UserA);
        var userB = await world.UserIdAsync(world.UserB);
        var userAB = await world.UserIdAsync(world.UserAB);
        await MakeAdminAsync(world, userA, world.A.WorkspaceId);
        var inviteB = await InviteAsync(system, world.B.WorkspaceId, TenantMembership.Member);

        var adminA = await world.CookieAsync(world.UserA);
        var a = world.A.WorkspaceId;
        var b = world.B.WorkspaceId;

        // They see and manage A only.
        var tenants = await JsonAsync(await adminA.GetAsync("/api/admin/tenants", Ct), HttpStatusCode.OK);
        Assert.Equal([a], tenants.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()));
        var members = await JsonAsync(await adminA.GetAsync($"/api/admin/tenants/{a}/members", Ct), HttpStatusCode.OK);
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == userA && m.GetProperty("role").GetString() == "admin");

        // Every route into B is refused, and nothing in B moved.
        var intoB = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/api/admin/tenants/{b}/members", null),
            (HttpMethod.Get, $"/api/admin/tenants/{b}/invites", null),
            (HttpMethod.Post, $"/api/admin/tenants/{b}/invites", new { role = "admin" }),
            (HttpMethod.Put, $"/api/admin/tenants/{b}/members/{userB}", new { role = "admin" }),
            (HttpMethod.Delete, $"/api/admin/tenants/{b}/members/{userB}", null),
            (HttpMethod.Delete, $"/api/admin/tenants/{b}/invites/{inviteB.Id}", null),
        };
        foreach (var (method, path, body) in intoB)
        {
            var refused = await SendAsync(adminA, method, path, body);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} → {(int)refused.StatusCode}");
            Assert.Contains("GET /api/admin/tenants", await ErrorAsync(refused), StringComparison.Ordinal);
        }

        Assert.Equal(TenantMembership.Member, await RoleAsync(world, userB, b));
        Assert.Equal(1, await PendingAsync(world, b));

        // B's invite and B's user through A's path: A has neither.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(adminA, HttpMethod.Delete, $"/api/admin/tenants/{a}/invites/{inviteB.Id}")).StatusCode);
        Assert.Equal(1, await PendingAsync(world, b));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(adminA, HttpMethod.Put, $"/api/admin/tenants/{a}/members/{userB}", new { role = "admin" })).StatusCode);
        Assert.Null(await RoleAsync(world, userB, a));

        // In A they do what an admin does, and a change in A leaves the same user's role in B alone.
        await InviteAsync(adminA, a, TenantMembership.Admin);
        var promoted = await JsonAsync(await SendAsync(adminA, HttpMethod.Put, $"/api/admin/tenants/{a}/members/{userAB}", new { role = "admin" }), HttpStatusCode.OK);
        Assert.True(promoted.GetProperty("changed").GetBoolean());
        Assert.Equal(TenantMembership.Admin, await RoleAsync(world, userAB, a));
        Assert.Equal(TenantMembership.Member, await RoleAsync(world, userAB, b));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(adminA, HttpMethod.Delete, $"/api/admin/tenants/{a}/members/{userAB}")).StatusCode);
        Assert.Null(await RoleAsync(world, userAB, a));
        Assert.Equal(TenantMembership.Member, await RoleAsync(world, userAB, b));

        // What only a system administrator does.
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/api/admin/tenants"),
                     (HttpMethod.Get, "/api/admin/users"),
                     (HttpMethod.Post, $"/api/admin/users/{userB}/disable"),
                     (HttpMethod.Post, $"/api/admin/users/{userB}/enable"),
                 })
        {
            var refused = await SendAsync(adminA, method, path, method == HttpMethod.Post ? new { slug = NewSlug("nope") } : null);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} → {(int)refused.StatusCode}");
            Assert.Contains("system administrator", await ErrorAsync(refused), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_member_gets_403_on_every_Admin_route()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var userB = await world.UserIdAsync(world.UserB);
        var b = world.B.WorkspaceId;
        var inviteB = await InviteAsync(system, b, TenantMembership.Member);

        var member = await world.CookieAsync(world.UserB);
        var me = await JsonAsync(await member.GetAsync("/api/session/me", Ct), HttpStatusCode.OK);
        Assert.False(me.GetProperty("administers").GetBoolean());
        Assert.Equal(world.UserB, me.GetProperty("name").GetString());

        foreach (var (method, path, body) in Routes(b, userB, inviteB.Id))
        {
            var refused = await SendAsync(member, method, path, body);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} → {(int)refused.StatusCode}");
            Assert.Contains("administers no workspace", await ErrorAsync(refused), StringComparison.Ordinal);

            // Without a cookie it is 401, and a bearer token is not a cookie.
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(server.NewClient(), method, path, body)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(world.Bearer(ReadServer.ControllerToken), method, path, body)).StatusCode);
        }

        Assert.Equal(1, await PendingAsync(world, b));
        Assert.Equal(TenantMembership.Member, await RoleAsync(world, userB, b));
    }

    [Fact]
    public async Task A_system_Admin_reaches_every_tenant_and_every_user()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var userB = await world.UserIdAsync(world.UserB);

        var me = await JsonAsync(await system.GetAsync("/api/session/me", Ct), HttpStatusCode.OK);
        Assert.True(me.GetProperty("systemAdmin").GetBoolean());
        Assert.True(me.GetProperty("administers").GetBoolean());

        var tenants = (await JsonAsync(await system.GetAsync("/api/admin/tenants", Ct), HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Contains(tenants, t => t.GetProperty("id").GetGuid() == world.A.WorkspaceId && t.GetProperty("current").GetBoolean());
        Assert.Contains(tenants, t => t.GetProperty("id").GetGuid() == world.B.WorkspaceId && !t.GetProperty("current").GetBoolean());

        var members = await JsonAsync(await system.GetAsync($"/api/admin/tenants/{world.B.WorkspaceId}/members", Ct), HttpStatusCode.OK);
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("name").GetString() == world.UserB);
        Assert.Equal(HttpStatusCode.NotFound, (await system.GetAsync($"/api/admin/tenants/{Guid.CreateVersion7()}/members", Ct)).StatusCode);

        var users = (await JsonAsync(await system.GetAsync("/api/admin/users", Ct), HttpStatusCode.OK)).EnumerateArray().ToList();
        var ab = users.Single(u => u.GetProperty("name").GetString() == world.UserAB);
        Assert.Equal([world.A.WorkspaceId, world.B.WorkspaceId], ab.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()));

        // Disabling ends the user's session at its next request; enabling lets them sign in again.
        var cookieB = await world.CookieAsync(world.UserB);
        await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userB}/disable"), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookieB.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppServer.SignInAsync(server.NewClient(), world.UserB, TenantWorld.Password, Ct)).StatusCode);
        await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userB}/enable"), HttpStatusCode.OK);
        await world.CookieAsync(world.UserB);

        // A system administrator cannot disable themselves.
        var self = me.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{self}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{Guid.CreateVersion7()}/disable")).StatusCode);
    }

    [Fact]
    public async Task The_last_Admin_of_a_tenant_cannot_be_removed_or_demoted()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var tenant = await CreateTenantAsync(system, NewSlug("last"));

        var first = NewName("first");
        await JsonAsync(await AcceptAsync(server.NewClient(), (await InviteAsync(system, tenant, TenantMembership.Admin)).Token, first, NewPassword), HttpStatusCode.OK);
        var firstId = (await UserNamedAsync(world, first))!.Value;

        var demote = await SendAsync(system, HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{firstId}", new { role = "member" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Contains("last admin", await ErrorAsync(demote), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(system, HttpMethod.Delete, $"/api/admin/tenants/{tenant}/members/{firstId}")).StatusCode);
        Assert.Equal(TenantMembership.Admin, await RoleAsync(world, firstId, tenant));

        // A second admin frees the first; then the second is the last.
        var second = NewName("second");
        await JsonAsync(await AcceptAsync(server.NewClient(), (await InviteAsync(system, tenant, TenantMembership.Member)).Token, second, NewPassword), HttpStatusCode.OK);
        var secondId = (await UserNamedAsync(world, second))!.Value;
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(system, HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{secondId}", new { role = "owner" })).StatusCode);
        await JsonAsync(await SendAsync(system, HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{secondId}", new { role = "admin" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(system, HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{firstId}", new { role = "member" }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(system, HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{secondId}", new { role = "member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(system, HttpMethod.Delete, $"/api/admin/tenants/{tenant}/members/{secondId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(system, HttpMethod.Delete, $"/api/admin/tenants/{tenant}/members/{firstId}")).StatusCode);

        // Two admins demoting each other at once: one wins, the workspace keeps an admin.
        await JsonAsync(await AcceptAsync(server.NewClient(), (await InviteAsync(system, tenant, TenantMembership.Admin)).Token, NewName("third"), NewPassword), HttpStatusCode.OK);
        var admins = (await JsonAsync(await system.GetAsync($"/api/admin/tenants/{tenant}/members", Ct), HttpStatusCode.OK))
            .EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()).ToList();
        Assert.Equal(2, admins.Count);
        var raced = await Task.WhenAll(admins.Select(id => SendAsync(system, HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{id}", new { role = "member" })));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], raced.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await AdminCountAsync(world, tenant));
    }

    [Fact]
    public async Task Admin_writes_need_the_write_header()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);

        var unguarded = await SendAsync(system, HttpMethod.Post, $"/api/admin/tenants/{world.B.WorkspaceId}/invites", new { role = "member" }, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, unguarded.StatusCode);
        Assert.Contains("X-WorkState-Write", await ErrorAsync(unguarded), StringComparison.Ordinal);
        Assert.Equal(0, await PendingAsync(world, world.B.WorkspaceId));
    }

    /// <summary>Every <c>/api/admin</c> route, aimed at real rows of <paramref name="tenant"/>.</summary>
    private static (HttpMethod Method, string Path, object? Body)[] Routes(Guid tenant, Guid user, Guid invite) =>
    [
        (HttpMethod.Get, "/api/admin/tenants", null),
        (HttpMethod.Post, "/api/admin/tenants", new { slug = NewSlug("member") }),
        (HttpMethod.Get, $"/api/admin/tenants/{tenant}/members", null),
        (HttpMethod.Put, $"/api/admin/tenants/{tenant}/members/{user}", new { role = "admin" }),
        (HttpMethod.Delete, $"/api/admin/tenants/{tenant}/members/{user}", null),
        (HttpMethod.Get, $"/api/admin/tenants/{tenant}/invites", null),
        (HttpMethod.Post, $"/api/admin/tenants/{tenant}/invites", new { role = "admin" }),
        (HttpMethod.Delete, $"/api/admin/tenants/{tenant}/invites/{invite}", null),
        (HttpMethod.Get, "/api/admin/users", null),
        (HttpMethod.Post, $"/api/admin/users/{user}/disable", null),
        (HttpMethod.Post, $"/api/admin/users/{user}/enable", null),
    ];

    private static async Task<int> PendingAsync(TenantWorld world, Guid tenant)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_link WHERE workspace_id = @tenant AND used_at IS NULL AND expires_at > now();",
            new { tenant });
    }

    private static async Task<int> AdminCountAsync(TenantWorld world, Guid tenant)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM tenant_membership WHERE workspace_id = @tenant AND role = 'admin';",
            new { tenant });
    }
}

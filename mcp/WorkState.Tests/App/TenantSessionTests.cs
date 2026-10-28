using System.Net;
using System.Text.Json;
using WorkState.Api.App;
using WorkState.Api.Auth;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The dashboard cookie's tenant: where a sign-in lands, who may switch it where, and that losing a membership
/// ends the session in that tenant on its next request rather than when the cookie expires.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TenantSessionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_member_of_two_tenants_lands_in_the_first_and_switches_to_the_other()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var cookie = await world.CookieAsync(world.UserAB);

        var tenants = await TenantsAsync(cookie);
        Assert.Equal([world.A.WorkspaceId, world.B.WorkspaceId], tenants.Select(t => t.Id));
        Assert.Equal([true, false], tenants.Select(t => t.Current));
        Assert.All(tenants, t => Assert.Equal("member", t.Role));
        Assert.Equal([world.A.ProjectId], await ProjectsAsync(cookie));

        Assert.Equal(HttpStatusCode.NoContent, (await SwitchAsync(cookie, world.B.WorkspaceId)).StatusCode);

        Assert.Equal([world.B.ProjectId], await ProjectsAsync(cookie));
        Assert.Equal([false, true], (await TenantsAsync(cookie)).Select(t => t.Current));

        // /r resolves in the tenant switched to.
        var link = await cookie.GetAsync("/r/run-a/1.1", Ct);
        Assert.Equal($"/runs/{world.B.RunId}/{world.B.TaskId}", link.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_tenant_switch_is_refused_to_a_non_member_and_allowed_to_a_system_admin()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;

        var memberOfA = await world.CookieAsync(world.UserA);
        var refused = await SwitchAsync(memberOfA, world.B.WorkspaceId);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("GET /api/session/tenants", await ErrorAsync(refused), StringComparison.Ordinal);
        Assert.Equal([world.A.ProjectId], await ProjectsAsync(memberOfA));
        Assert.Equal([world.A.WorkspaceId], (await TenantsAsync(memberOfA)).Select(t => t.Id));

        // An id that is no workspace at all is refused the same way, and the write header is still required.
        Assert.Equal(HttpStatusCode.Forbidden, (await SwitchAsync(memberOfA, Guid.CreateVersion7())).StatusCode);
        var unguarded = await memberOfA.PostAsync("/api/session/tenant", AppServer.Json(new { id = world.A.WorkspaceId }), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, unguarded.StatusCode);

        // The operator is the system administrator and a member of A only; B is theirs to enter.
        var admin = await server.SignInAsync(Ct);
        var adminTenants = await TenantsAsync(admin);
        Assert.Contains(adminTenants, t => t.Id == world.B.WorkspaceId && t.Role == UserTenant.SystemAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await SwitchAsync(admin, world.B.WorkspaceId)).StatusCode);
        Assert.Equal([world.B.ProjectId], await ProjectsAsync(admin));

        // A bearer token is no session to switch.
        var bearer = world.Bearer(ReadServer.ControllerToken);
        var switched = new HttpRequestMessage(HttpMethod.Post, "/api/session/tenant") { Content = AppServer.Json(new { id = world.B.WorkspaceId }) };
        switched.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.SendAsync(switched, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_cookie_whose_membership_is_removed_stops_working_in_that_tenant_on_its_next_request()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var cookie = await world.CookieAsync(world.UserAB);
        Assert.Equal(HttpStatusCode.NoContent, (await SwitchAsync(cookie, world.B.WorkspaceId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cookie.GetAsync("/api/v1/overview", Ct)).StatusCode);

        await world.LeaveAsync(world.UserAB, world.B.WorkspaceId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await cookie.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookie.GetAsync("/api/session", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SwitchAsync(cookie, world.A.WorkspaceId)).StatusCode);

        // Signing in again lands in the tenant still theirs.
        var again = await world.CookieAsync(world.UserAB);
        Assert.Equal([world.A.ProjectId], await ProjectsAsync(again));
        Assert.Equal([world.A.WorkspaceId], (await TenantsAsync(again)).Select(t => t.Id));
    }

    [Fact]
    public async Task A_user_who_is_a_member_of_no_tenant_cannot_sign_in()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await world.LeaveAsync(world.UserA, world.A.WorkspaceId);

        var client = server.NewClient();
        var response = await AppServer.SignInAsync(client, world.UserA, TenantWorld.Password, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("not a member of any workspace", await ErrorAsync(response), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }

    /// <summary>A path that exists only in another tenant the user may enter: named, with the switch.</summary>
    [Fact]
    public async Task A_link_into_another_tenant_the_user_may_enter_names_it_and_offers_the_switch()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var cookie = await world.CookieAsync(world.UserAB);

        var response = await cookie.GetAsync($"/r/{TenantWorld.OnlyInBPath}", Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains($"<code>{world.B.Slug}</code>", page, StringComparison.Ordinal);
        Assert.Contains($"data-tenant=\"{world.B.WorkspaceId}\"", page, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/session/tenant'", page, StringComparison.Ordinal);
        Assert.DoesNotContain(world.OnlyInBId.ToString(), page, StringComparison.OrdinalIgnoreCase);

        // What the button does, then the same URL again.
        Assert.Equal(HttpStatusCode.NoContent, (await SwitchAsync(cookie, world.B.WorkspaceId)).StatusCode);
        var followed = await cookie.GetAsync($"/r/{TenantWorld.OnlyInBPath}", Ct);
        Assert.Equal($"/runs/{world.B.RunId}/{world.OnlyInBId}", followed.Headers.Location!.OriginalString);
    }

    private sealed record Tenant(Guid Id, string Slug, string Role, bool Current);

    private static async Task<List<Tenant>> TenantsAsync(HttpClient cookie)
    {
        var body = await AppServer.ReadJsonAsync(cookie, "/api/session/tenants", Ct);
        return
        [
            .. body.EnumerateArray().Select(t => new Tenant(
                t.GetProperty("id").GetGuid(),
                t.GetProperty("slug").GetString()!,
                t.GetProperty("role").GetString()!,
                t.GetProperty("current").GetBoolean())),
        ];
    }

    private static async Task<List<Guid>> ProjectsAsync(HttpClient client)
        => [.. (await AppServer.ReadJsonAsync(client, "/api/v1/overview", Ct)).GetProperty("projects").EnumerateArray()
            .Select(p => p.GetProperty("item").GetProperty("id").GetGuid())];

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient cookie, Guid tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/tenant") { Content = AppServer.Json(new { id = tenant }) };
        request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        return cookie.SendAsync(request, Ct);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;
}

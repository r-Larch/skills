using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using WorkState.Core.Domain;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.AdminApi;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// Invite links: a system administrator makes a workspace and a link into it, the link makes a user with that
/// membership — once, within 14 days — or gives a signed-in user the membership, and its token is never stored.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class InviteTests(PostgresFixture fixture)
{
    private const string NewPassword = "invited-user-password-81c2";

    [Fact]
    public async Task A_system_admin_creates_a_tenant_and_an_Invite_that_creates_a_user_with_the_membership_exactly_once()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var admin = await server.SignInAsync(Ct);

        var slug = NewSlug("invited");
        var tenant = await CreateTenantAsync(admin, slug);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(admin, HttpMethod.Post, "/api/admin/tenants", new { slug })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(admin, HttpMethod.Post, "/api/admin/tenants", new { slug = "Not A Slug" })).StatusCode);

        var invite = await InviteAsync(admin, tenant, TenantMembership.Member);
        Assert.Equal(slug, invite.Tenant);
        Assert.Equal($"{server.BaseAddress}/join/{invite.Token}", invite.Url);

        // The pending list names the invite and never its token.
        var pendingBody = await (await admin.GetAsync($"/api/admin/tenants/{tenant}/invites", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains(invite.Id.ToString(), pendingBody, StringComparison.Ordinal);
        Assert.DoesNotContain(invite.Token, pendingBody, StringComparison.Ordinal);

        var visitor = server.NewClient();
        var described = await JsonAsync(await visitor.GetAsync($"/api/invite/{invite.Token}", Ct), HttpStatusCode.OK);
        Assert.Equal(slug, described.GetProperty("tenant").GetString());
        Assert.Equal(TenantMembership.Member, described.GetProperty("role").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, described.GetProperty("signedInAs").ValueKind);

        // A refused accept uses nothing: a short password, then no write header.
        var name = NewName("invitee");
        var weak = await AcceptAsync(visitor, invite.Token, name, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("at least 12 characters", await ErrorAsync(weak), StringComparison.Ordinal);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(visitor, HttpMethod.Post, $"/api/invite/{invite.Token}/accept", new { name, password = NewPassword }, header: false)).StatusCode);
        Assert.Null(await UserNamedAsync(world, name));

        var accepted = await JsonAsync(await AcceptAsync(visitor, invite.Token, name, NewPassword), HttpStatusCode.OK);
        Assert.Equal(name, accepted.GetProperty("name").GetString());

        // Signed in, in that workspace and nowhere else.
        Assert.Equal([tenant], await TenantIdsAsync(visitor));
        Assert.Equal(tenant, await CurrentTenantAsync(visitor));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/api/v1/overview", Ct)).StatusCode);
        var user = await UserNamedAsync(world, name);
        Assert.NotNull(user);
        Assert.Equal(TenantMembership.Member, await RoleAsync(world, user!.Value, tenant));

        // The same link a second time makes nobody.
        var second = server.NewClient();
        var other = NewName("second");
        var again = await AcceptAsync(second, invite.Token, other, NewPassword);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Contains("used already, revoked, or it expired", await ErrorAsync(again), StringComparison.Ordinal);
        Assert.Null(await UserNamedAsync(world, other));
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync($"/api/invite/{invite.Token}", Ct)).StatusCode);
        Assert.Equal("[]", await (await admin.GetAsync($"/api/admin/tenants/{tenant}/invites", Ct)).Content.ReadAsStringAsync(Ct));

        // The new user signs in with what they chose.
        Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(server.NewClient(), name, NewPassword, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_Invite_is_valid_for_14_days_and_refused_after()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var admin = await server.SignInAsync(Ct);
        var invite = await InviteAsync(admin, world.B.WorkspaceId, TenantMembership.Member);

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var lifetime = await connection.ExecuteScalarAsync<double>(
            "SELECT extract(epoch FROM expires_at - created_at) FROM user_link WHERE id = @Id;", new { invite.Id });
        Assert.Equal(TimeSpan.FromDays(14).TotalSeconds, lifetime, 1.0);

        // Fourteen days and a second on, as if that time had passed.
        await connection.ExecuteAsync(
            """
            UPDATE user_link SET created_at = created_at - interval '14 days 1 second',
                                 expires_at = expires_at - interval '14 days 1 second'
            WHERE id = @Id;
            """,
            new { invite.Id });

        var visitor = server.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/invite/{invite.Token}", Ct)).StatusCode);
        var name = NewName("late");
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(visitor, invite.Token, name, NewPassword)).StatusCode);
        Assert.Null(await UserNamedAsync(world, name));
        Assert.Equal("[]", await (await admin.GetAsync($"/api/admin/tenants/{world.B.WorkspaceId}/invites", Ct)).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_signed_in_user_accepting_an_Invite_gets_the_membership_and_lands_in_it()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var admin = await server.SignInAsync(Ct);
        var userA = await world.UserIdAsync(world.UserA);
        var invite = await InviteAsync(admin, world.B.WorkspaceId, TenantMembership.Admin);

        var cookie = await world.CookieAsync(world.UserA);
        var described = await JsonAsync(await cookie.GetAsync($"/api/invite/{invite.Token}", Ct), HttpStatusCode.OK);
        Assert.Equal(world.UserA, described.GetProperty("signedInAs").GetString());
        Assert.False(described.GetProperty("member").GetBoolean());

        // Signed in, the body is not a new user: the cookie's user accepts.
        var accepted = await JsonAsync(await AcceptAsync(cookie, invite.Token), HttpStatusCode.OK);
        Assert.Equal(world.UserA, accepted.GetProperty("name").GetString());

        Assert.Equal(TenantMembership.Admin, await RoleAsync(world, userA, world.B.WorkspaceId));
        Assert.Equal(TenantMembership.Member, await RoleAsync(world, userA, world.A.WorkspaceId));
        Assert.Equal([world.A.WorkspaceId, world.B.WorkspaceId], await TenantIdsAsync(cookie));
        Assert.Equal(world.B.WorkspaceId, await CurrentTenantAsync(cookie));

        // An invite never lowers a role already held: an admin of B accepting a member invite stays admin.
        var lower = await InviteAsync(admin, world.B.WorkspaceId, TenantMembership.Member);
        Assert.True((await JsonAsync(await cookie.GetAsync($"/api/invite/{lower.Token}", Ct), HttpStatusCode.OK)).GetProperty("member").GetBoolean());
        await JsonAsync(await AcceptAsync(cookie, lower.Token), HttpStatusCode.OK);
        Assert.Equal(TenantMembership.Admin, await RoleAsync(world, userA, world.B.WorkspaceId));
    }

    [Fact]
    public async Task An_Invite_for_a_name_already_taken_is_refused_and_stays_usable()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var admin = await server.SignInAsync(Ct);
        var invite = await InviteAsync(admin, world.B.WorkspaceId, TenantMembership.Member);
        var visitor = server.NewClient();

        var taken = await AcceptAsync(visitor, invite.Token, world.UserA.ToUpperInvariant(), NewPassword);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("sign in first", await ErrorAsync(taken), StringComparison.Ordinal);
        Assert.Null(await RoleAsync(world, await world.UserIdAsync(world.UserA), world.B.WorkspaceId));

        await JsonAsync(await AcceptAsync(visitor, invite.Token, NewName("fresh"), NewPassword), HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_Invite_token_is_stored_only_as_its_hash()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var admin = await server.SignInAsync(Ct);
        var invite = await InviteAsync(admin, world.B.WorkspaceId, TenantMembership.Member);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(invite.Token)));

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        Assert.Equal(hash, await connection.ExecuteScalarAsync<string>("SELECT token_hash FROM user_link WHERE id = @Id;", new { invite.Id }));
        Assert.Empty(await TablesHoldingAsync(connection, invite.Token));

        await JsonAsync(await AcceptAsync(server.NewClient(), invite.Token, NewName("hashed"), NewPassword), HttpStatusCode.OK);
        Assert.Empty(await TablesHoldingAsync(connection, invite.Token));
    }

    [Fact]
    public async Task Invite_links_are_rate_limited_per_client()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var guesser = server.NewClient();
        var guess = new string('A', 43);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await guesser.GetAsync($"/api/invite/{guess}", Ct)).StatusCode);
        }

        var throttled = await AcceptAsync(guesser, guess, NewName("guess"), NewPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.RetryAfter is not null);
    }

    /// <summary>Every table in the schema whose rows, as JSON, contain <paramref name="secret"/>.</summary>
    internal static async Task<List<string>> TablesHoldingAsync(Npgsql.NpgsqlConnection connection, string secret)
    {
        var tables = await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE';");

        var holding = new List<string>();
        foreach (var table in tables)
        {
            if (await connection.ExecuteScalarAsync<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM \"{table}\" x WHERE strpos(row_to_json(x)::text, @secret) > 0);",
                    new { secret }))
            {
                holding.Add(table);
            }
        }

        return holding;
    }
}

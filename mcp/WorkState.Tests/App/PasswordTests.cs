using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Identity;
using WorkState.Core.Domain;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.AdminApi;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// Password change and reset links: a change needs the current password and ends the user's other cookies but
/// not the one that changed it; a reset link works once, for 24 hours, is stored only hashed, ends every cookie,
/// and is made by a tenant admin only for a user wholly inside their workspaces. OAuth tokens outlive both.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class PasswordTests(PostgresFixture fixture)
{
    private const string NewPassword = "a-new-password-for-tests-6b2d";

    [Fact]
    public async Task A_Password_change_needs_the_current_one_and_ends_the_other_cookies_but_not_this_one()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var first = await world.CookieAsync(world.UserA);
        var second = await world.CookieAsync(world.UserA);
        var oauth = world.Bearer(await world.OAuthAsync(first, $"{server.BaseAddress}/api", "workstate.read offline_access"));

        // A wrong current password is sign-in's refusal, a bare 401, and changes nothing.
        var wrong = await ChangeAsync(first, "not-the-current-password", NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(string.Empty, await wrong.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/overview", Ct)).StatusCode);

        var weak = await ChangeAsync(first, TenantWorld.Password, "too-short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("at least 12 characters", await ErrorAsync(weak), StringComparison.Ordinal);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(first, HttpMethod.Post, "/api/session/password", new { current = TenantWorld.Password, next = NewPassword }, header: false)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(first, TenantWorld.Password, NewPassword)).StatusCode);

        // This cookie was re-issued with the new stamp; the other one is signed out; the OAuth grant stays.
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.GetAsync("/api/session", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await oauth.GetAsync("/api/v1/overview", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await AppServer.SignInAsync(server.NewClient(), world.UserA, TenantWorld.Password, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(server.NewClient(), world.UserA, NewPassword, Ct)).StatusCode);
        Assert.Equal(PasswordVerificationResult.Success, await VerifyAsync(world, world.UserA, NewPassword));
    }

    [Fact]
    public async Task A_Password_reset_link_works_once_for_24_hours_and_is_stored_only_hashed()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var userB = await world.UserIdAsync(world.UserB);

        var reset = await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userB}/reset-link"), HttpStatusCode.Created);
        var token = reset.GetProperty("token").GetString()!;
        Assert.Equal(world.UserB, reset.GetProperty("name").GetString());
        Assert.Equal($"{server.BaseAddress}/reset/{token}", reset.GetProperty("url").GetString());

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var hash = Sha256(token);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM user_link WHERE token_hash = @hash AND kind = 'reset';", new { hash }));
        Assert.Empty(await InviteTests.TablesHoldingAsync(connection, token));
        var lifetime = await connection.ExecuteScalarAsync<double>(
            "SELECT extract(epoch FROM expires_at - created_at) FROM user_link WHERE token_hash = @hash;", new { hash });
        Assert.Equal(TimeSpan.FromHours(24).TotalSeconds, lifetime, 1.0);

        var visitor = server.NewClient();
        var described = await JsonAsync(await visitor.GetAsync($"/api/reset/{token}", Ct), HttpStatusCode.OK);
        Assert.Equal(world.UserB, described.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await UseResetAsync(visitor, token, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(visitor, HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword }, header: false)).StatusCode);
        await JsonAsync(await UseResetAsync(visitor, token, NewPassword), HttpStatusCode.OK);

        // Once: a second use, even with another password, changes nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await UseResetAsync(visitor, token, "yet-another-password-0000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/reset/{token}", Ct)).StatusCode);
        Assert.Equal(PasswordVerificationResult.Success, await VerifyAsync(world, world.UserB, NewPassword));
        Assert.Empty(await InviteTests.TablesHoldingAsync(connection, token));

        // A newer link replaces an unused older one, and a link 24 hours old is refused.
        var older = (await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userB}/reset-link"), HttpStatusCode.Created)).GetProperty("token").GetString()!;
        var newer = (await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userB}/reset-link"), HttpStatusCode.Created)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await UseResetAsync(visitor, older, "older-link-password-1111")).StatusCode);
        await connection.ExecuteAsync(
            """
            UPDATE user_link SET created_at = created_at - interval '24 hours 1 second',
                                 expires_at = expires_at - interval '24 hours 1 second'
            WHERE token_hash = @hash;
            """,
            new { hash = Sha256(newer) });
        Assert.Equal(HttpStatusCode.NotFound, (await UseResetAsync(visitor, newer, "expired-link-password-22")).StatusCode);
        Assert.Equal(PasswordVerificationResult.Success, await VerifyAsync(world, world.UserB, NewPassword));
    }

    [Fact]
    public async Task A_Password_reset_ends_every_cookie_of_the_user_and_keeps_their_OAuth_grant()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var userB = await world.UserIdAsync(world.UserB);
        var one = await world.CookieAsync(world.UserB);
        var two = await world.CookieAsync(world.UserB);
        var oauth = world.Bearer(await world.OAuthAsync(one, $"{server.BaseAddress}/api", "workstate.read offline_access"));

        var token = (await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userB}/reset-link"), HttpStatusCode.Created))
            .GetProperty("token").GetString()!;
        await JsonAsync(await UseResetAsync(server.NewClient(), token, NewPassword), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Unauthorized, (await one.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await two.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await oauth.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await system.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_tenant_admin_of_A_cannot_create_a_Password_reset_link_for_anyone_in_B()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var system = await server.SignInAsync(Ct);
        var operatorId = (await JsonAsync(await system.GetAsync("/api/session/me", Ct), HttpStatusCode.OK)).GetProperty("id").GetGuid();
        var userA = await world.UserIdAsync(world.UserA);
        var userB = await world.UserIdAsync(world.UserB);
        var userAB = await world.UserIdAsync(world.UserAB);
        await MakeAdminAsync(world, userA, world.A.WorkspaceId);
        var adminA = await world.CookieAsync(world.UserA);

        // A user only in A is theirs to reset.
        var onlyInA = NewName("only-a");
        var invite = await InviteAsync(adminA, world.A.WorkspaceId, TenantMembership.Member);
        await JsonAsync(await AcceptAsync(server.NewClient(), invite.Token, onlyInA, NewPassword), HttpStatusCode.OK);
        var onlyInAId = (await UserNamedAsync(world, onlyInA))!.Value;
        await JsonAsync(await SendAsync(adminA, HttpMethod.Post, $"/api/admin/users/{onlyInAId}/reset-link"), HttpStatusCode.Created);

        // Anyone with a foot in B, a system administrator, or no one at all: the same refusal.
        foreach (var target in new[] { userB, userAB, operatorId, Guid.CreateVersion7() })
        {
            var refused = await SendAsync(adminA, HttpMethod.Post, $"/api/admin/users/{target}/reset-link");
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"reset for {target} → {(int)refused.StatusCode}");
            Assert.Contains("Ask a system administrator", await ErrorAsync(refused), StringComparison.Ordinal);
        }

        Assert.Equal(0, await ResetCountAsync(world, userB, userAB, operatorId));

        // A member reaches no reset at all, not even their own.
        var member = await world.CookieAsync(world.UserB);
        var own = await SendAsync(member, HttpMethod.Post, $"/api/admin/users/{userB}/reset-link");
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Contains("administers no workspace", await ErrorAsync(own), StringComparison.Ordinal);

        // A system administrator resets anyone, and is told about an id that is no user.
        await JsonAsync(await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{userAB}/reset-link"), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(system, HttpMethod.Post, $"/api/admin/users/{Guid.CreateVersion7()}/reset-link")).StatusCode);
    }

    private static string Sha256(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient cookie, string current, string next)
        => SendAsync(cookie, HttpMethod.Post, "/api/session/password", new { current, next });

    private static Task<HttpResponseMessage> UseResetAsync(HttpClient client, string token, string password)
        => SendAsync(client, HttpMethod.Post, $"/api/reset/{token}", new { password });

    private static async Task<PasswordVerificationResult> VerifyAsync(TenantWorld world, string name, string password)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        var hash = await connection.ExecuteScalarAsync<string>("SELECT password_hash FROM app_user WHERE lower(name) = lower(@name);", new { name });
        return new PasswordHasher<AppUser>().VerifyHashedPassword(new AppUser(), hash!, password);
    }

    private static async Task<int> ResetCountAsync(TenantWorld world, params Guid[] users)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_link WHERE kind = 'reset' AND user_id = ANY(@users);",
            new { users });
    }
}

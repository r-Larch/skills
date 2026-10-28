using System.Diagnostics;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkState.Api.Auth;
using WorkState.Core.Domain;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The dashboard's users: the configured credential creates the first one and is never consulted again, and
/// from then on <c>app_user</c> alone decides who signs in and whose cookie still works. Each test owns a
/// <see cref="FreshDatabase"/>, because the shared one's first user belongs to whichever test signed in first.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class UserTests(PostgresFixture fixture)
{
    private sealed record UserRow(Guid Id, string Name, string PasswordHash, bool IsSystemAdmin);

    [Fact]
    public async Task The_first_sign_in_creates_one_system_admin_who_administers_every_workspace()
    {
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        var slug = $"users-{Guid.CreateVersion7():n}";
        var workspaces = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };

        await using (var server = await StartAsync(database, slug))
        {
            using (var connection = database.Open())
            {
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM app_user;"));
                await connection.ExecuteAsync(
                    "INSERT INTO workspace (id, slug) VALUES (@a, @slug), (@b, @other);",
                    new { a = workspaces[0], slug, b = workspaces[1], other = $"{slug}-other" });
            }

            var client = await server.SignInAsync(Ct);
            Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/session", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(server.NewClient(), AppServer.Username, AppServer.Password, Ct)).StatusCode);
        }

        var first = await SingleUserAsync(database);
        Assert.Equal(AppServer.Username, first.Name);
        Assert.True(first.IsSystemAdmin);
        Assert.DoesNotContain(AppServer.Password, first.PasswordHash, StringComparison.Ordinal);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<AppUser>().VerifyHashedPassword(new AppUser(), first.PasswordHash, AppServer.Password));

        using (var connection = database.Open())
        {
            var memberships = (await connection.QueryAsync<(Guid WorkspaceId, string Role)>(
                "SELECT workspace_id, role FROM tenant_membership WHERE user_id = @Id ORDER BY workspace_id;",
                new { first.Id })).ToList();

            Assert.Equal(workspaces.Order(), memberships.Select(m => m.WorkspaceId));
            Assert.All(memberships, m => Assert.Equal(TenantMembership.Admin, m.Role));
        }

        // A restart is a new host on the same database: it signs the same user in and seeds nothing.
        await using (var restarted = await StartAsync(database, slug))
        {
            await restarted.SignInAsync(Ct);
        }

        var after = await SingleUserAsync(database);
        Assert.Equal(first, after);
    }

    [Fact]
    public async Task Once_a_user_exists_the_configured_password_is_never_consulted_again()
    {
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        var slug = $"users-{Guid.CreateVersion7():n}";
        const string changed = "a-new-configured-password-7a1e";

        // A jar outside the server's own clients, which it disposes when it stops.
        using var signedIn = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await using (var server = await StartAsync(database, slug))
        {
            signedIn.BaseAddress = new Uri(server.BaseAddress);
            Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(signedIn, AppServer.Username, AppServer.Password, Ct)).StatusCode);
        }

        await using (var server = await StartAsync(database, slug, password: changed))
        {
            var client = server.NewClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await AppServer.SignInAsync(client, AppServer.Username, changed, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct)).StatusCode);
        }

        // With no credential configured at all, the user and the session they already hold still work.
        await using (var server = await AppServer.StartAsync(database.ConnectionString, slug, Ct, dashboardConfigured: false))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await signedIn.GetAsync($"{server.BaseAddress}/api/session", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(server.NewClient(), AppServer.Username, AppServer.Password, Ct)).StatusCode);
        }

        await SingleUserAsync(database);
    }

    [Fact]
    public async Task A_wrong_password_an_unknown_user_and_a_disabled_user_get_the_same_401_in_comparable_time()
    {
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        await using var server = await StartAsync(database, $"users-{Guid.CreateVersion7():n}");
        await server.SignInAsync(Ct);

        const string disabledPassword = "the-disabled-users-password-41c";
        await InsertUserAsync(database, "disabled-user", disabledPassword, disabled: true);

        var attempts = new (string Name, string Password)[]
        {
            (AppServer.Username, "not-the-password"),
            ("nobody-by-this-name", AppServer.Password),
            ("disabled-user", disabledPassword),
        };

        var responses = new List<(HttpStatusCode Status, string Headers, string Body)>();
        foreach (var (name, password) in attempts)
        {
            var response = await AppServer.SignInAsync(server.NewClient(), name, password, Ct);
            responses.Add((
                response.StatusCode,
                string.Join('\n', response.Headers.Where(h => h.Key != "Date").Select(h => $"{h.Key}: {string.Join(',', h.Value)}")),
                await response.Content.ReadAsStringAsync(Ct)));
        }

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
        Assert.Single(responses.Distinct());

        // Timed on the store, past the endpoint's rate limit. Each refusal runs one hash verification, so the
        // unknown and the disabled user cost what a wrong password does instead of a bare lookup's millisecond.
        var users = server.Services.GetRequiredService<UserStore>();
        var dashboard = server.Services.GetRequiredService<DashboardOptions>();
        var medians = new List<double>();
        foreach (var (name, password) in attempts)
        {
            var samples = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                Assert.Null(await users.SignInAsync(name, password, dashboard, Ct));
                samples.Add(clock.Elapsed.TotalMilliseconds);
            }

            medians.Add(samples.Order().ElementAt(2));
        }

        Assert.All(medians, m => Assert.True(m > medians[0] / 2, $"medians {string.Join(", ", medians)} ms"));
    }

    [Fact]
    public async Task A_cookie_whose_user_is_disabled_stops_working()
    {
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        await using var server = await StartAsync(database, $"users-{Guid.CreateVersion7():n}");
        var client = await server.SignInAsync(Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/session", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);

        using (var connection = database.Open())
        {
            await connection.ExecuteAsync("UPDATE app_user SET disabled_at = now();");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);

        // Nor does the configured credential sign them back in: it was spent on creating them.
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_create_one_user()
    {
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        await using var server = await StartAsync(database, $"users-{Guid.CreateVersion7():n}");
        var users = server.Services.GetRequiredService<UserStore>();
        var dashboard = server.Services.GetRequiredService<DashboardOptions>();

        // On the store, because the endpoint's rate limit admits five a minute from one address.
        var signedIn = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => users.SignInAsync(AppServer.Username, AppServer.Password, dashboard, Ct), Ct)));

        var user = await SingleUserAsync(database);
        Assert.All(signedIn, u => Assert.Equal(user.Id, u?.Id));

        // And through the endpoint, on a second database, as many as the rate limit lets through at once.
        await using var other = await FreshDatabase.CreateAsync(fixture, Ct);
        await using var second = await StartAsync(other, $"users-{Guid.CreateVersion7():n}");
        var responses = await Task.WhenAll(Enumerable.Range(0, RateLimits.SignInWindow.PermitLimit).Select(_ =>
            AppServer.SignInAsync(second.NewClient(), AppServer.Username, AppServer.Password, Ct)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        await SingleUserAsync(other);
    }

    private static Task<AppServer> StartAsync(FreshDatabase database, string slug, string? password = null)
        => AppServer.StartAsync(
            database.ConnectionString,
            slug,
            Ct,
            configure: password is null
                ? null
                : builder => builder.Configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["WorkState:Dashboard:Password"] = password }));

    private static async Task<UserRow> SingleUserAsync(FreshDatabase database)
    {
        using var connection = database.Open();
        return Assert.Single(await connection.QueryAsync<UserRow>(
            """SELECT id AS "Id", name AS "Name", password_hash AS "PasswordHash", is_system_admin AS "IsSystemAdmin" FROM app_user;"""));
    }

    private static async Task InsertUserAsync(FreshDatabase database, string name, string password, bool disabled)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync(
            """
            INSERT INTO app_user (id, name, password_hash, disabled_at)
            VALUES (@id, @name, @hash, CASE WHEN @disabled THEN now() END);
            """,
            new
            {
                id = Guid.CreateVersion7(),
                name,
                hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), password),
                disabled,
            });
    }
}

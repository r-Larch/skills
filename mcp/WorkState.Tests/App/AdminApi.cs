using System.Net;
using System.Text.Json;
using Dapper;
using WorkState.Api.App;
using WorkState.Core.Domain;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The admin and link API as the dashboard calls it: every write with <see cref="ProjectEndpoints.WriteHeader"/>,
/// every answer read as JSON. Shared by the Admin, Invite and Password tests.
/// </summary>
internal static class AdminApi
{
    public sealed record Invite(Guid Id, string Token, string Url, string Role, string Tenant, DateTimeOffset ExpiresAt);

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, bool header = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = AppServer.Json(body);
        }

        if (header)
        {
            request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        }

        return client.SendAsync(request, Ct);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} → {(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<string> ErrorAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;

    public static async Task<Guid> CreateTenantAsync(HttpClient systemAdmin, string slug)
    {
        var created = await JsonAsync(
            await SendAsync(systemAdmin, HttpMethod.Post, "/api/admin/tenants", new { slug }),
            HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    public static async Task<Invite> InviteAsync(HttpClient admin, Guid tenant, string role)
    {
        var created = await JsonAsync(
            await SendAsync(admin, HttpMethod.Post, $"/api/admin/tenants/{tenant}/invites", new { role }),
            HttpStatusCode.Created);
        return new Invite(
            created.GetProperty("id").GetGuid(),
            created.GetProperty("token").GetString()!,
            created.GetProperty("url").GetString()!,
            created.GetProperty("role").GetString()!,
            created.GetProperty("tenant").GetString()!,
            created.GetProperty("expiresAt").GetDateTimeOffset());
    }

    public static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string token, string? name = null, string? password = null)
        => SendAsync(client, HttpMethod.Post, $"/api/invite/{token}/accept", name is null ? new { } : new { name, password });

    public static async Task<List<Guid>> TenantIdsAsync(HttpClient cookie)
        => [.. (await AppServer.ReadJsonAsync(cookie, "/api/session/tenants", Ct)).EnumerateArray().Select(t => t.GetProperty("id").GetGuid())];

    public static async Task<Guid> CurrentTenantAsync(HttpClient cookie)
        => (await AppServer.ReadJsonAsync(cookie, "/api/session/tenants", Ct)).EnumerateArray()
            .Single(t => t.GetProperty("current").GetBoolean()).GetProperty("id").GetGuid();

    public static async Task<string?> RoleAsync(TenantWorld world, Guid user, Guid tenant)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT role FROM tenant_membership WHERE user_id = @user AND workspace_id = @tenant;",
            new { user, tenant });
    }

    public static async Task MakeAdminAsync(TenantWorld world, Guid user, Guid tenant)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            "UPDATE tenant_membership SET role = @admin WHERE user_id = @user AND workspace_id = @tenant;",
            new { user, tenant, admin = TenantMembership.Admin });
    }

    public static async Task<Guid?> UserNamedAsync(TenantWorld world, string name)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM app_user WHERE lower(name) = lower(@name);", new { name });
    }

    /// <summary>Random, not version 7: two names made in one millisecond must still differ.</summary>
    public static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():n}"[..(prefix.Length + 13)];

    public static string NewSlug(string prefix) => $"{prefix}-{Guid.NewGuid():n}";
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using WorkState.Api.App;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// Two tenants that share every ref — the project <see cref="Project"/>, <c>run-a</c>, <c>1</c>, <c>1.1</c> —
/// and a marker word in both task titles, so a read that crosses over shows up as the other tenant's title.
/// B alone also holds <see cref="OnlyInB"/>, which a caller of A must find nowhere. The server's static tokens
/// are bound to A, and the operator (the system administrator, and the OAuth subject) is a member of A only.
/// </summary>
internal sealed class TenantWorld
{
    public const string Marker = "quokka";
    public const string Password = "tenant-user-password-5d71";

    public const string TitleA = "quokka task of tenant A";
    public const string TitleB = "quokka task of tenant B";
    public const string TitleOnlyInB = "quokka only in tenant B";

    public const string Project = "tenant-proj";
    public const string OnlyInB = "1.9";

    private TenantWorld(PostgresFixture fixture, WriteWorld a, WriteWorld b, Guid onlyInBId, AppServer server)
    {
        Fixture = fixture;
        A = a;
        B = b;
        OnlyInBId = onlyInBId;
        Server = server;
    }

    public PostgresFixture Fixture { get; }

    public WriteWorld A { get; }

    public WriteWorld B { get; }

    public Guid OnlyInBId { get; }

    public AppServer Server { get; }

    /// <summary>B's own item, fully qualified as a caller of B names it; to a caller of A it names nothing.</summary>
    public static string OnlyInBPath => $"{Project}/run-a/{OnlyInB}";

    public string UserA { get; } = $"tenant-a-{Guid.CreateVersion7():n}"[..20];

    public string UserB { get; } = $"tenant-b-{Guid.CreateVersion7():n}"[..20];

    /// <summary>A member of both, A first.</summary>
    public string UserAB { get; } = $"tenant-ab-{Guid.CreateVersion7():n}"[..21];

    public static async Task<TenantWorld> CreateAsync(PostgresFixture fixture)
    {
        var a = await WriteSeed.CreateAsync(fixture);
        var b = await WriteSeed.CreateAsync(fixture);
        var onlyInB = await WriteSeed.ChildAsync(b, b.PhaseId, OnlyInB);

        await using (var db = fixture.NewContext())
        {
            (await db.Items.SingleAsync(i => i.Id == a.TaskId, Ct)).Title = TitleA;
            (await db.Items.SingleAsync(i => i.Id == b.TaskId, Ct)).Title = TitleB;
            (await db.Items.SingleAsync(i => i.Id == onlyInB, Ct)).Title = TitleOnlyInB;
            (await db.Items.SingleAsync(i => i.Id == a.ProjectId, Ct)).Ref = Project;
            (await db.Items.SingleAsync(i => i.Id == b.ProjectId, Ct)).Ref = Project;
            await db.SaveChangesAsync(Ct);
        }

        var server = await AppServer.StartAsync(fixture.ConnectionString, a.Slug, Ct);
        var world = new TenantWorld(fixture, a, b, onlyInB, server);

        await world.UserAsync(world.UserA, a.WorkspaceId);
        await world.UserAsync(world.UserB, b.WorkspaceId);
        await world.UserAsync(world.UserAB, a.WorkspaceId, b.WorkspaceId);
        return world;
    }

    private async Task UserAsync(string name, params Guid[] workspaces)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(Ct);
        var id = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            "INSERT INTO app_user (id, name, password_hash) VALUES (@id, @name, @hash);",
            new { id, name, hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Password) });

        // One statement each, so created_at — the order a sign-in lands by — follows the arguments.
        foreach (var workspace in workspaces)
        {
            await connection.ExecuteAsync(
                "INSERT INTO tenant_membership (user_id, workspace_id, role, created_at) VALUES (@id, @workspace, 'member', clock_timestamp());",
                new { id, workspace });
        }
    }

    public async Task MemberAsync(string name, Guid workspace)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO tenant_membership (user_id, workspace_id, role)
            SELECT id, @workspace, 'member' FROM app_user WHERE lower(name) = lower(@name);
            """,
            new { name, workspace });
    }

    public async Task LeaveAsync(string name, Guid workspace)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            DELETE FROM tenant_membership m USING app_user u
            WHERE m.user_id = u.id AND lower(u.name) = lower(@name) AND m.workspace_id = @workspace;
            """,
            new { name, workspace });
    }

    public async Task<HttpClient> CookieAsync(string name)
    {
        var client = Server.NewClient();
        var response = await AppServer.SignInAsync(client, name, Password, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    /// <summary>Every credential kind, each acting in tenant A.</summary>
    public async Task<IReadOnlyList<Credential>> CredentialsOfAAsync()
    {
        var browser = await Server.SignInAsync(Ct);
        var mcp = await OAuthAsync(browser, $"{Server.BaseAddress}/mcp", "mcp offline_access");
        var api = await OAuthAsync(browser, $"{Server.BaseAddress}/api", "workstate.read workstate.sessions offline_access");
        var minted = await MintAsync(Bearer(ReadServer.ControllerToken), $"tenant-{Guid.CreateVersion7():n}"[..20], "controller");

        return
        [
            new Credential("cookie", await CookieAsync(UserA), null),
            new Credential("oauth", Bearer(api), mcp),
            new Credential("wss", Bearer(minted), minted),
            new Credential("static", Bearer(ReadServer.ControllerToken), ReadServer.ControllerToken),
        ];
    }

    public HttpClient Bearer(string token)
    {
        var client = Server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<string> MintAsync(HttpClient bearer, string name, string role)
    {
        var response = await bearer.PostAsync("/api/agent-sessions", AppServer.Json(new { name, role }), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"mint → {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>The operator's authorization code flow, as <see cref="OAuthTests"/> runs it, for one resource.</summary>
    public async Task<string> OAuthAsync(HttpClient browser, string resource, string scope)
    {
        var anonymous = Server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);
        var pkce = OAuthTests.Pkce.Create();

        var authorize = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = "http://127.0.0.1:7777/callback",
            ["scope"] = scope,
            ["state"] = "state-1",
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = resource,
        });

        var response = await browser.GetAsync(authorize, Ct);
        if (response.Headers.Location!.OriginalString.StartsWith("/connect/consent?", StringComparison.Ordinal))
        {
            response = await OAuthTests.AnswerConsentAsync(browser, response.Headers.Location!, "granted");
        }

        var code = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString();
        return (await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce)).AccessToken;
    }

    public Task<McpClient> McpAsync(string token) => Server.ConnectAsync(token, Ct);

    public async Task<int> ItemCountAsync(Guid workspace)
    {
        await using var db = Fixture.NewContext();
        return await db.Items.CountAsync(i => i.WorkspaceId == workspace, Ct);
    }

    public async Task<ItemStatus> StatusAsync(Guid itemId)
    {
        await using var db = Fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == itemId, Ct)).Status;
    }

    public Task<WriteResult> NoteAsync(WriteWorld world, string body)
        => world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "observation", body, "1.1", null, Ct);
}

/// <summary>One way of calling the server: <see cref="Http"/> for <c>/api</c>, <see cref="Mcp"/> for <c>/mcp</c>, if it reaches it.</summary>
internal sealed record Credential(string Kind, HttpClient Http, string? Mcp)
{
    public override string ToString() => Kind;
}

using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 02 §15b, the matrix's world: one tenant, two projects that share every ref below them — <c>run-a</c>, <c>1</c>,
/// <c>1.1</c> — with a marker word in both task titles, so a read that crosses over shows up as the other project's
/// title. <see cref="User1"/> is a member of <see cref="P1"/> only, <see cref="User2"/> of both; the static tokens
/// have no user and see both; the operator administers the tenant and is a member of neither. P2 alone holds
/// <see cref="OnlyInP2"/>, a note, and a repository binding.
/// </summary>
internal sealed class ProjectWorld
{
    public const string Marker = "quokka";
    public const string Password = "project-user-password-8c42";

    public const string P1 = "pa-one";
    public const string P2 = "pa-two";
    public const string OnlyInP2 = "1.9";

    public const string TitleP1 = "quokka task of P1";
    public const string TitleP2 = "quokka task of P2";
    public const string TitleOnlyInP2 = "quokka only in P2";
    public const string NoteP2 = "quokka note into P2";
    public const string RemoteP2 = "https://example.invalid/project-access/pa-two.git";

    private ProjectWorld(PostgresFixture fixture, WriteWorld one, AppServer server)
    {
        Fixture = fixture;
        One = one;
        Server = server;
    }

    public PostgresFixture Fixture { get; }

    /// <summary>The workspace and P1's tree.</summary>
    public WriteWorld One { get; }

    public Guid WorkspaceId => One.WorkspaceId;

    public Guid P1Id => One.ProjectId;

    public Guid P2Id { get; private set; }

    public Guid P2RunId { get; private set; }

    public Guid P2PhaseId { get; private set; }

    public Guid P2TaskId { get; private set; }

    public Guid OnlyInP2Id { get; private set; }

    public AppServer Server { get; }

    public string User1 { get; } = $"pa-u1-{Guid.CreateVersion7():n}"[..18];

    public string User2 { get; } = $"pa-u2-{Guid.CreateVersion7():n}"[..18];

    public Guid User1Id { get; private set; }

    public Guid User2Id { get; private set; }

    public static string TaskP2Path => $"{P2}/run-a/1.1";

    public static string OnlyInP2Path => $"{P2}/run-a/{OnlyInP2}";

    /// <summary>Every id of P2's tree: what a non-member must find nowhere.</summary>
    public HashSet<Guid> P2Ids => [P2Id, P2RunId, P2PhaseId, P2TaskId, OnlyInP2Id];

    public static async Task<ProjectWorld> CreateAsync(PostgresFixture fixture, int extraOpenInP2 = 0)
    {
        var one = await WriteSeed.CreateAsync(fixture);
        await using (var db = fixture.NewContext())
        {
            (await db.Items.SingleAsync(i => i.Id == one.ProjectId, Ct)).Ref = P1;
            (await db.Items.SingleAsync(i => i.Id == one.TaskId, Ct)).Title = TitleP1;
            await db.SaveChangesAsync(Ct);
        }

        var server = await AppServer.StartAsync(fixture.ConnectionString, one.Slug, Ct);
        var world = new ProjectWorld(fixture, one, server);
        await world.SeedP2Async(extraOpenInP2);

        world.User1Id = await world.UserAsync(world.User1);
        world.User2Id = await world.UserAsync(world.User2);
        await world.MembersAsync(world.P1Id, world.User1Id, world.User2Id);
        await world.MembersAsync(world.P2Id, world.User2Id);

        var note = await one.Writer.NoteAsync(one.WorkspaceId, WriteSeed.Orchestrator, "observation", NoteP2, TaskP2Path, null, Ct);
        Assert.False(note.Refused, note.Text);
        return world;
    }

    private async Task SeedP2Async(int extraOpen)
    {
        await using var db = Fixture.NewContext();
        var project = Seed.NewItem(WorkspaceId, null, "project", P2, 2m);
        var run = Seed.NewItem(WorkspaceId, project.Id, "run", "run-a", 1m, "The other project's run");
        var phase = Seed.NewItem(WorkspaceId, run.Id, "phase", "1", 1m, "Foundations of P2");
        var task = Seed.NewItem(WorkspaceId, phase.Id, "task", "1.1", 1m, TitleP2);
        var only = Seed.NewItem(WorkspaceId, phase.Id, "task", OnlyInP2, 9m, TitleOnlyInP2);
        task.VerifyCmd = only.VerifyCmd = "dotnet test --filter P2";
        db.Items.AddRange(project, run, phase, task, only);
        for (var n = 0; n < extraOpen; n++)
        {
            var extra = Seed.NewItem(WorkspaceId, phase.Id, "task", $"1.{20 + n}", 20 + n, $"quokka backlog of P2 #{n}");
            extra.VerifyCmd = "dotnet test --filter P2";
            db.Items.Add(extra);
        }

        await db.SaveChangesAsync(Ct);
        db.RunConfigs.Add(new RunConfig { ItemId = run.Id, ProjectItemId = project.Id, SerializationKey = $"tree-{run.Id:n}" });
        await db.SaveChangesAsync(Ct);

        foreach (var key in RepoKeys.Of(RemoteP2, null))
        {
            db.ProjectRepos.Add(new ProjectRepo
            {
                Id = Guid.CreateVersion7(), WorkspaceId = WorkspaceId, ProjectItemId = project.Id, Kind = key.Kind, Key = key.Key,
            });
        }

        await db.SaveChangesAsync(Ct);
        (P2Id, P2RunId, P2PhaseId, P2TaskId, OnlyInP2Id) = (project.Id, run.Id, phase.Id, task.Id, only.Id);
    }

    private async Task<Guid> UserAsync(string name)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(Ct);
        var id = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            """
            INSERT INTO app_user (id, name, password_hash) VALUES (@id, @name, @hash);
            INSERT INTO tenant_membership (user_id, workspace_id, role) VALUES (@id, @workspace, 'member');
            """,
            new { id, name, hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Password), workspace = WorkspaceId });
        return id;
    }

    private async Task MembersAsync(Guid project, params Guid[] users)
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(Ct);
        foreach (var user in users)
        {
            await connection.ExecuteAsync(
                "INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@project, @user, @workspace);",
                new { project, user, workspace = WorkspaceId });
        }
    }

    public async Task<HttpClient> CookieAsync(string name)
    {
        var client = Server.NewClient();
        var response = await AppServer.SignInAsync(client, name, Password, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    /// <summary>
    /// The user's three credential kinds: their cookie, an OAuth token issued to that cookie (for <c>/api</c>, with
    /// one for <c>/mcp</c> beside it), and a <c>wss_</c> session that OAuth token minted, which is theirs too.
    /// </summary>
    public async Task<IReadOnlyList<Credential>> CredentialsAsync(string user)
    {
        var cookie = await CookieAsync(user);
        var mcp = await OAuthAsync(cookie, $"{Server.BaseAddress}/mcp", "mcp offline_access");
        var api = await OAuthAsync(cookie, $"{Server.BaseAddress}/api", "workstate.read workstate.sessions offline_access");
        var minted = await TenantWorld.MintAsync(Bearer(api), $"pa-{Guid.CreateVersion7():n}"[..20], "controller");

        return
        [
            new Credential($"{user} cookie", cookie, null),
            new Credential($"{user} oauth", Bearer(api), mcp),
            new Credential($"{user} wss", Bearer(minted), minted),
        ];
    }

    /// <summary>A static token: no user, so every project of its tenant.</summary>
    public Credential Static => new("static", Bearer(ReadServer.ControllerToken), ReadServer.ControllerToken);

    public HttpClient Bearer(string token)
    {
        var client = Server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The authorization code flow for one resource, as <see cref="TenantWorld"/> runs it, from <paramref name="browser"/>.</summary>
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

    /// <summary>A journal event on <paramref name="itemRef"/>, written as the owner would: a note addressed to it.</summary>
    public async Task NoteAsync(string itemRef, string body)
    {
        var note = await One.Writer.NoteAsync(WorkspaceId, WriteSeed.Orchestrator, "observation", body, itemRef, null, Ct);
        Assert.False(note.Refused, note.Text);
    }

    /// <summary>What P2's tree holds, counted as the owner: a write that leaked into it changes one of these.</summary>
    public async Task<(int Items, int Notes, int Criteria, int Links, int Verifications, int Assumptions, int Rules)> P2CountsAsync()
    {
        await using var connection = await Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleAsync<(int, int, int, int, int, int, int)>(
            """
            SELECT (SELECT count(*) FROM item WHERE id = @p2 OR ancestors @> ARRAY[@p2]::uuid[])::int,
                   (SELECT count(*) FROM note n JOIN item i ON i.id = n.to_item_id OR i.id = n.from_item_id
                    WHERE i.id = @p2 OR i.ancestors @> ARRAY[@p2]::uuid[])::int,
                   (SELECT count(*) FROM item_criterion c JOIN item i ON i.id = c.item_id
                    WHERE i.id = @p2 OR i.ancestors @> ARRAY[@p2]::uuid[])::int,
                   (SELECT count(*) FROM item_link l JOIN item i ON i.id = l.from_item_id OR i.id = l.to_item_id
                    WHERE i.id = @p2 OR i.ancestors @> ARRAY[@p2]::uuid[])::int,
                   (SELECT count(*) FROM verification v JOIN item i ON i.id = v.item_id
                    WHERE i.id = @p2 OR i.ancestors @> ARRAY[@p2]::uuid[])::int,
                   (SELECT count(*) FROM assumption a JOIN item i ON i.id = a.scope_item_id
                    WHERE i.id = @p2 OR i.ancestors @> ARRAY[@p2]::uuid[])::int,
                   (SELECT count(*) FROM constraint_rule r JOIN item i ON i.id = r.scope_item_id
                    WHERE i.id = @p2 OR i.ancestors @> ARRAY[@p2]::uuid[])::int;
            """,
            new { p2 = P2Id });
    }

    public async Task<ItemStatus> StatusAsync(Guid itemId)
    {
        await using var db = Fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == itemId, Ct)).Status;
    }
}

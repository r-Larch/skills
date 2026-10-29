using System.Net;
using System.Text.Json;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 10, the overview's <c>sharing</c>: who shares each project, read under the caller's own tenant transaction,
/// so a project hidden from them (doc 02 §15b) gives up neither its existence nor its member count (C66, C67).
/// The world is <see cref="ProjectWorld"/>'s, plus a project no one has joined.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SharingTests(PostgresFixture fixture)
{
    private const string Open = "pa-open";

    [Fact]
    public async Task Sharing_the_overview_counts_each_projects_members_and_says_whether_the_caller_is_one()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var open = await OpenProjectAsync(world);

        var two = await OverviewAsync(await world.CookieAsync(world.User2));
        Assert.Equal((2, true), Sharing(two, world.P1Id));
        Assert.Equal((1, true), Sharing(two, world.P2Id));
        Assert.Equal((0, false), Sharing(two, open));
        Assert.Equal([world.User1, world.User2], Names(two, world.P1Id));
        Assert.Equal([world.User2], Names(two, world.P2Id));
        Assert.Empty(Names(two, open));

        var one = await OverviewAsync(await world.CookieAsync(world.User1));
        Assert.Equal((2, true), Sharing(one, world.P1Id));
        Assert.Null(Project(one, world.P2Id));
        Assert.Equal((0, false), Sharing(one, open));

        // A static token names no user: every project, and the caller is a member of none.
        var anonymous = await OverviewAsync(world.Static.Http);
        Assert.Equal((2, false), Sharing(anonymous, world.P1Id));
        Assert.Equal((1, false), Sharing(anonymous, world.P2Id));
    }

    [Fact]
    public async Task Sharing_a_non_member_reads_no_member_count_of_a_hidden_project_even_by_its_id()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        var body = await (await world.CookieAsync(world.User1)).GetStringAsync("/api/v1/overview", Ct);
        Assert.DoesNotContain(world.P2Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProjectWorld.P2, body, StringComparison.Ordinal);

        // The query itself, asked for both ids: the policy, not the caller's list of ids, keeps P2 out.
        var board = new BoardQueries(fixture.DataSource);
        using (TenantScope.EnterAs(world.WorkspaceId, world.User1Id))
        {
            var rows = await board.ProjectSharingAsync([world.P1Id, world.P2Id], Ct);
            Assert.Equal([world.P1Id], rows.Select(r => r.ProjectId));
        }

        using (TenantScope.EnterAs(world.WorkspaceId, world.User2Id))
        {
            var rows = await board.ProjectSharingAsync([world.P1Id, world.P2Id], Ct);
            Assert.Equal([(world.P1Id, 2, true), (world.P2Id, 1, true)], rows.Select(r => (r.ProjectId, r.Members, r.You)).Order());
        }
    }

    [Fact]
    public async Task Sharing_an_admin_who_is_not_a_member_manages_the_members_but_the_overview_leaves_the_project_out()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var open = await OpenProjectAsync(world);
        var admin = await server.SignInAsync(Ct);
        var members = $"/api/projects/{world.P2Id}/members";

        var listed = await AdminApi.JsonAsync(await AdminApi.SendAsync(admin, HttpMethod.Get, members, header: false), HttpStatusCode.OK);
        Assert.Equal([world.User2], listed.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("name").GetString()));

        var added = await AdminApi.JsonAsync(
            await AdminApi.SendAsync(admin, HttpMethod.Post, members, new { userId = world.User1Id }), HttpStatusCode.OK);
        Assert.Equal(2, added.GetProperty("members").GetArrayLength());

        // Not a member of either project: the overview lists only the one no one has joined, and P2's work is a typo.
        var overview = await OverviewAsync(admin);
        Assert.Null(Project(overview, world.P1Id));
        Assert.Null(Project(overview, world.P2Id));
        Assert.Equal((0, false), Sharing(overview, open));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/items/{world.P2Id}", Ct)).StatusCode);

        // The member the admin added now sees it, shared with two.
        Assert.Equal((2, true), Sharing(await OverviewAsync(await world.CookieAsync(world.User1)), world.P2Id));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await AdminApi.SendAsync(admin, HttpMethod.Delete, $"{members}/{world.User1Id}")).StatusCode);
        Assert.Null(Project(await OverviewAsync(await world.CookieAsync(world.User1)), world.P2Id));
    }

    /// <summary>A project created as the owner, with no member: open to the whole workspace.</summary>
    private static async Task<Guid> OpenProjectAsync(ProjectWorld world)
    {
        await using var db = world.Fixture.NewContext();
        var project = Seed.NewItem(world.WorkspaceId, null, "project", Open, 3m);
        db.Items.Add(project);
        await db.SaveChangesAsync(Ct);
        return project.Id;
    }

    private static Task<JsonElement> OverviewAsync(HttpClient client) => AppServer.ReadJsonAsync(client, "/api/v1/overview", Ct);

    private static JsonElement? Project(JsonElement overview, Guid id)
    {
        foreach (var project in overview.GetProperty("projects").EnumerateArray())
        {
            if (project.GetProperty("item").GetProperty("id").GetGuid() == id)
            {
                return project;
            }
        }

        return null;
    }

    private static (int Members, bool You) Sharing(JsonElement overview, Guid id)
    {
        var sharing = (Project(overview, id) ?? throw new Xunit.Sdk.XunitException($"the overview does not list {id}"))
            .GetProperty("sharing");
        return (sharing.GetProperty("members").GetInt32(), sharing.GetProperty("you").GetBoolean());
    }

    private static List<string?> Names(JsonElement overview, Guid id)
        => [.. Project(overview, id)!.Value.GetProperty("sharing").GetProperty("names").EnumerateArray().Select(n => n.GetString())];
}

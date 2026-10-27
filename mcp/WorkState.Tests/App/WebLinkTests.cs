using System.Net;
using ModelContextProtocol.Client;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// Unit <c>backlog/web-links</c>: <c>/r/{path}</c> resolves a ref the way <c>work_get</c> does and
/// redirects to its dashboard page, behind the dashboard sign-in; <c>work_brief</c> and
/// <c>work_get</c> print that link as a <c>web</c> line, and the line opens the page it names.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class WebLinkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Project_run_item_and_shorter_paths_redirect_to_their_dashboard_pages()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        var loose = await WriteSeed.ChildAsync(world, world.ProjectId, "loose", kind: "followup");

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var client = await server.SignInAsync(Ct);

        Assert.Equal($"/projects/{world.ProjectId}", await LocationAsync(client, $"/r/{project}"));
        Assert.Equal($"/runs/{world.RunId}", await LocationAsync(client, $"/r/{project}/run-a"));
        Assert.Equal($"/runs/{world.RunId}/{world.TaskId}", await LocationAsync(client, $"/r/{project}/run-a/1.1"));
        Assert.Equal($"/runs/{world.RunId}/{world.PhaseId}", await LocationAsync(client, $"/r/{project}/run-a/1"));

        // The shorter forms work_get accepts land on the same page.
        Assert.Equal($"/runs/{world.RunId}/{world.TaskId}", await LocationAsync(client, "/r/1.1"));
        Assert.Equal($"/runs/{world.RunId}/{world.TaskId}", await LocationAsync(client, "/r/run-a/1.1"));
        Assert.Equal($"/runs/{world.RunId}", await LocationAsync(client, "/r/run-a"));

        // Outside every run there is no board to select it on.
        Assert.Equal($"/items/{loose}", await LocationAsync(client, $"/r/{project}/loose"));
    }

    [Fact]
    public async Task Unknown_and_ambiguous_paths_answer_with_a_page_that_says_so_and_links_the_candidates()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        await WriteSeed.ChildAsync(world, world.ProjectId, "1.1", kind: "followup");

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var client = await server.SignInAsync(Ct);

        var ambiguous = await client.GetAsync("/r/1.1", Ct);
        var page = await ambiguous.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.MultipleChoices, ambiguous.StatusCode);
        Assert.Null(ambiguous.Headers.Location);
        Assert.Contains("names 2 items", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/r/run-a/1.1\"", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/r/{project}/1.1\"", page, StringComparison.Ordinal);

        // Each candidate link is itself a path /r resolves to one item.
        Assert.Equal($"/runs/{world.RunId}/{world.TaskId}", await LocationAsync(client, "/r/run-a/1.1"));

        var unknown = await client.GetAsync("/r/no-such-thing", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Null(unknown.Headers.Location);
        Assert.Contains("Nothing has the path <code>no-such-thing</code>", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var escaped = await client.GetAsync("/r/%3Cscript%3E", Ct);
        Assert.DoesNotContain("<script>", await escaped.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_dashboard_cookie_r_serves_the_sign_in_page_and_the_same_url_redirects_once_signed_in()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        var link = $"/r/{project}/run-a/1.1";

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var anonymous = server.NewClient();
        var refused = await anonymous.GetAsync(link, Ct);
        var page = await refused.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Null(refused.Headers.Location);
        Assert.Contains("fetch('/api/session'", page, StringComparison.Ordinal);
        Assert.Contains("location.reload()", page, StringComparison.Ordinal);
        foreach (var id in new[] { world.ProjectId, world.RunId, world.TaskId })
        {
            Assert.DoesNotContain(id.ToString(), page, StringComparison.OrdinalIgnoreCase);
        }

        // An agent's bearer token is for /mcp; it does not open the dashboard.
        var bearer = server.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", ReadServer.ControllerToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync(link, Ct)).StatusCode);

        // What the page's script does: sign in on the same origin, then load the same URL again.
        Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(anonymous, AppServer.Username, AppServer.Password, Ct)).StatusCode);
        Assert.Equal($"/runs/{world.RunId}/{world.TaskId}", await LocationAsync(anonymous, link));
    }

    [Fact]
    public async Task Work_brief_and_work_get_print_a_web_line_whose_link_opens_the_right_page()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var cookie = await server.SignInAsync(Ct);
        await using var mcp = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var expected = new (string Scope, string Full, string Page)[]
        {
            ("1.1", $"{project}/run-a/1.1", $"/runs/{world.RunId}/{world.TaskId}"),
            ("run-a", $"{project}/run-a", $"/runs/{world.RunId}"),
            (project, project, $"/projects/{world.ProjectId}"),
        };

        foreach (var (scope, full, page) in expected)
        {
            foreach (var variant in new[] { "resume", "worker" })
            {
                var brief = await Call(mcp, "work_brief", new() { ["scope"] = scope, ["for"] = variant });
                Assert.Equal($"web      {server.BaseAddress}/r/{full}", WebLine(brief, "web      "));
                Assert.Equal(page, await LocationAsync(cookie, Path(server, WebLine(brief, "web      ")[9..])));
            }

            var got = await Call(mcp, "work_get", new() { ["refs"] = new[] { scope } });
            Assert.Equal($"  web       {server.BaseAddress}/r/{full}", WebLine(got, "  web       "));
            Assert.Equal(page, await LocationAsync(cookie, Path(server, WebLine(got, "  web       ")[12..])));
        }

        var gate = await Call(mcp, "work_brief", new() { ["scope"] = "1", ["for"] = "gate" });
        Assert.Equal($"web      {server.BaseAddress}/r/{project}/run-a/1", WebLine(gate, "web      "));

        // One web line per item block, after its uri and brief lines.
        var both = await Call(mcp, "work_get", new() { ["refs"] = new[] { "1.1", "run-a" } });
        var lines = both.Split('\n');
        Assert.Equal(2, lines.Count(l => l.StartsWith("  web       ", StringComparison.Ordinal)));
        var at = Array.FindIndex(lines, l => l.StartsWith("  web       ", StringComparison.Ordinal));
        Assert.StartsWith("  brief     ", lines[at - 1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_public_url_setting_replaces_the_request_host_in_the_web_line()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct, publicUrl: "https://workstate.example.test/");
        await using var mcp = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var brief = await Call(mcp, "work_brief", new() { ["scope"] = "1.1" });
        Assert.Equal($"web      https://workstate.example.test/r/{project}/run-a/1.1", WebLine(brief, "web      "));

        var got = await Call(mcp, "work_get", new() { ["refs"] = new[] { "1.1" } });
        Assert.Equal($"  web       https://workstate.example.test/r/{project}/run-a/1.1", WebLine(got, "  web       "));
    }

    [Fact]
    public async Task A_render_without_a_request_prints_no_web_line()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var got = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["1.1"], [], Ct);
        var brief = await world.Reader.BriefSessionAsync(await world.ScopeAsync("1.1"), "resume", WriteSeed.Keeper(fixture), Ct);

        Assert.DoesNotContain("web ", got, StringComparison.Ordinal);
        Assert.DoesNotContain("web ", brief, StringComparison.Ordinal);
    }

    private static async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = world.Fixture.NewContext();
        return (await db.Items.FindAsync([world.ProjectId], Ct))!.Ref;
    }

    private static async Task<string?> LocationAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect,
            $"{path} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return response.Headers.Location?.OriginalString;
    }

    private static string Path(AppServer server, string link)
    {
        Assert.StartsWith(server.BaseAddress + "/r/", link, StringComparison.Ordinal);
        return link[server.BaseAddress.Length..];
    }

    private static string WebLine(string text, string label)
        => Assert.Single(text.Split('\n'), l => l.StartsWith(label, StringComparison.Ordinal));

    private static Task<string> Call(McpClient client, string tool, Dictionary<string, object?> arguments)
        => ReadServer.CallAsync(client, tool, arguments, Ct);
}

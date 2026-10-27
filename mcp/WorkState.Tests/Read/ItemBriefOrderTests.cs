using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkState.Core.Writing;
using WorkState.Tests.App;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// gate-rest. <c>work://item/&lt;ref&gt;/brief</c> ordered an ambiguous ref's candidates around the
/// busiest run of the workspace, while <c>work_get</c> ordered the same ref's around the caller's own
/// run — so a session minted for one project was offered the other project's item first by one
/// surface and its own by the other.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class ItemBriefOrderTests(PostgresFixture fixture)
{
    private const string Root = @"P:\Projects\LarchSys\Other";

    [Fact]
    public async Task The_item_brief_resource_lists_an_ambiguous_refs_candidates_in_work_gets_order()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "The other repo", Ref: "other", Repo: new RepoArg(Root: Root))], Ct)).Refused);
        Assert.False((await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("run", "The other run", Ref: "other-run", Parent: "other")], Ct)).Refused);
        await WriteSeed.ChildAsync(world, (await world.ItemAsync("other-run")).Id, "1", kind: "phase");

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(await MintAsync(server), Ct);

        var got = await ReadServer.CallAsync(client, "work_get", new Dictionary<string, object?> { ["refs"] = new[] { "1" } }, Ct);
        var listed = GetList().Match(got);
        Assert.True(listed.Success, got);
        var fromTool = Quoted().Matches(listed.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();

        var read = await client.ReadResourceAsync("work://item/1/brief", cancellationToken: Ct);
        var text = string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
        var fromResource = WorkerBrief().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(["other-run/1", "run-a/1"], fromTool);
        Assert.Equal(fromTool, fromResource);

        foreach (var form in fromResource)
        {
            var brief = await ReadServer.CallAsync(
                client, "work_brief", new Dictionary<string, object?> { ["scope"] = form, ["for"] = "worker" }, Ct);
            Assert.DoesNotContain("REFUSED", brief, StringComparison.Ordinal);
        }
    }

    private static async Task<string> MintAsync(AppServer server)
    {
        var http = server.NewClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadServer.ControllerToken);

        var response = await http.PostAsync(
            "/api/agent-sessions",
            AppServer.Json(new { name = "aw-order", role = "orchestrator", repo = new { root = Root } }),
            Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    [GeneratedRegex("""work_get\(refs:\[([^\]]*)\]\)""")]
    private static partial Regex GetList();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex("""work_brief\(scope:"([^"]+)", for:"worker"\)""")]
    private static partial Regex WorkerBrief();
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.App;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// bare-brief. A hand-started session called <c>work_brief()</c> in one repo and was handed another
/// project's run: with several projects, the busiest run of the whole workspace is a confident wrong
/// answer. An unscoped call now either knows the caller's project or asks; and every ref a create or a
/// search prints resolves as printed.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class BareBriefTests(PostgresFixture fixture)
{
    private const string Chooser = "RUN —  ·  repo not given, and this workspace holds 3 projects";

    [Fact]
    public async Task A_bare_brief_over_three_projects_is_a_chooser_that_opens_no_run_and_writes_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await world.Writer.StatusAsync(world.WorkspaceId, "1.1", "in_progress", WriteSeed.Orchestrator, new StatusArgs(), Ct)).Refused);
        await WriteSeed.ExpireClaimAsync(world, world.TaskId);
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "for the task", "1.1", null, Ct)).Refused);
        await ProjectAsync(world, "beta");
        await ProjectAsync(world, "gamma");
        var events = await WriteSeed.EventKindsAsync(world);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        foreach (var variant in new[] { "resume", "worker" })
        {
            var text = await ReadServer.CallAsync(client, "work_brief", new Dictionary<string, object?> { ["for"] = variant }, Ct);
            var lines = text.Split('\n');

            Assert.Equal(Chooser, lines[0]);
            Assert.Contains("NEXT   work_brief(repo:{remote, root})", text, StringComparison.Ordinal);
            Assert.DoesNotContain("run-a", text, StringComparison.Ordinal);
            Assert.DoesNotContain("attention", text, StringComparison.Ordinal);

            var calls = Calls().Matches(text).Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(["gamma", "beta", await ProjectRefAsync(world)], calls);
            Assert.Matches("\n         work_brief\\(scope:\"gamma\"\\) +The gamma project  ·  1 open run, active just now\n", text);
        }

        // BriefSessionAsync sweeps expired claims and, for a worker brief, consumes the notes it
        // rendered. The chooser runs neither: the claim is still live, the note unread, the journal
        // unchanged.
        await using (var db = fixture.NewContext())
        {
            Assert.Null((await db.Claims.SingleAsync(c => c.ItemId == world.TaskId, Ct)).ReleasedAt);
            Assert.Null((await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).ConsumedAt);
        }

        Assert.Equal(events, await WriteSeed.EventKindsAsync(world));

        // Callable as printed: each opens that project's own brief.
        foreach (var project in new[] { "gamma", "beta", await ProjectRefAsync(world) })
        {
            var opened = await ReadServer.CallAsync(client, "work_brief", new Dictionary<string, object?> { ["scope"] = project }, Ct);
            Assert.StartsWith($"PROJECT {project}  ·", opened, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_bare_brief_over_one_project_or_none_is_the_busiest_run_as_before()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var bare = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        var scoped = await ReadServer.CallAsync(client, "work_brief", new Dictionary<string, object?> { ["scope"] = "run-a" }, Ct);
        Assert.Equal(
            scoped.Split('\n'),
            bare.Split('\n').Where(l => !l.StartsWith("repo     not given — work_brief(repo:{remote, root})", StringComparison.Ordinal)));
        Assert.NotEqual(scoped, bare);

        await using var empty = await ReadServer.StartAsync(fixture.ConnectionString, $"none-{Guid.CreateVersion7():n}", Ct);
        await using var cold = await empty.ConnectAsync(ReadServer.OrchestratorToken, Ct);
        Assert.Equal(BriefRenderer.Onboarding(), await ReadServer.CallAsync(cold, "work_brief", null, Ct));
    }

    [Fact]
    public async Task An_archived_or_cancelled_second_project_does_not_bring_up_the_chooser()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ProjectAsync(world, "beta");
        await ProjectAsync(world, "gamma");
        Assert.False((await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "beta", new UpdateArgs(Archived: true), Ct)).Refused);
        await using (var db = fixture.NewContext())
        {
            var gamma = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "gamma", Ct);
            gamma.Status = ItemStatus.Cancelled;
            gamma.ClosedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var first = (await ReadServer.CallAsync(client, "work_brief", null, Ct)).Split('\n')[0];
        Assert.Contains("run-a", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workspace's busiest run is run-a; the minted session's repo is project other's. Every
    /// unscoped read of that session lands in other, a static token keeps the workspace default, and
    /// the attention block follows the same choice.
    /// </summary>
    [Fact]
    public async Task A_minted_session_with_a_repo_reads_its_own_project_when_it_names_no_scope()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ProjectAsync(world, "other", new RepoArg(Remote, Root));
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "for run-a's task", "1.1", null, Ct)).Refused);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        await using var minted = await server.ConnectAsync(await MintAsync(server, "aw-bare", new { remote = Remote }), Ct);
        await using var shared = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        Assert.Contains("ready under other-run", await ReadServer.CallAsync(minted, "work_next", null, Ct), StringComparison.Ordinal);
        Assert.Contains("work_next(scope:\"other-run\")", await ReadServer.CallAsync(minted, "work_tree", null, Ct), StringComparison.Ordinal);
        Assert.Contains("under other-run", (await ReadServer.CallAsync(minted, "work_threads", null, Ct)).Split('\n')[0], StringComparison.Ordinal);
        Assert.StartsWith("PROJECT other  ·", await ResourceAsync(minted, WorkUris.WorkspaceBrief), StringComparison.Ordinal);

        Assert.Contains("ready under run-a", await ReadServer.CallAsync(shared, "work_next", null, Ct), StringComparison.Ordinal);

        const string note = "note(s) addressed to 1.1 unconsumed";
        var search = new Dictionary<string, object?> { ["q"] = "task" };
        Assert.DoesNotContain(note, await ReadServer.CallAsync(minted, "work_search", search, Ct), StringComparison.Ordinal);
        Assert.Contains(note, await ReadServer.CallAsync(shared, "work_search", search, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_get_carries_the_attention_of_its_first_refs_run()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        await ProjectAsync(world, "other");
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "over in run-b", "9.1", null, Ct)).Refused);
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "over in other", "other-run", null, Ct)).Refused);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var inRunB = await GetAsync(client, "9.1", "1.1");
        Assert.Contains("addressed to 9.1", inRunB, StringComparison.Ordinal);
        Assert.DoesNotContain("addressed to other-run", inRunB, StringComparison.Ordinal);

        var inRunA = await GetAsync(client, "1.1", "9.1");
        Assert.DoesNotContain("addressed to 9.1", inRunA, StringComparison.Ordinal);

        var inOther = await GetAsync(client, "other-run");
        Assert.Contains("addressed to other-run", inOther, StringComparison.Ordinal);
        Assert.DoesNotContain("addressed to 9.1", inOther, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live repro: a phase created with no ref under a new run is numbered "1", which run-a's
    /// first phase already is. Each printed ref — the created rows, the search rows and both Next
    /// lines — is called back exactly as printed.
    /// </summary>
    [Fact]
    public async Task Work_create_and_work_search_print_refs_that_resolve_as_printed()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var run = await CreateAsync(client, new { kind = "run", title = "Zebra run", @ref = "run-z", parent = await ProjectRefAsync(world) });
        Assert.Equal(["run-z"], CreatedRefs(run));
        Assert.Contains("work_status(ref:\"run-z\", to:\"in_progress\")", run, StringComparison.Ordinal);

        var phase = await CreateAsync(client, new { kind = "phase", title = "Zebra phase", parent = "run-z" });
        Assert.Equal(["run-z/1"], CreatedRefs(phase));
        Assert.Contains("work_status(ref:\"run-z/1\", to:\"in_progress\")", phase, StringComparison.Ordinal);
        Assert.Contains("Zebra phase", await ReadServer.CallAsync(client, "work_get", Refs("run-z/1"), Ct), StringComparison.Ordinal);

        var search = await ReadServer.CallAsync(client, "work_search", new Dictionary<string, object?> { ["q"] = "zebra phase" }, Ct);
        var hit = search.Split('\n')[2].Split(' ')[0];
        Assert.Equal("run-z/1", hit);
        var next = Assert.Single(NextGet().Matches(search)).Groups[1].Value;
        Assert.Equal(hit, next);
        Assert.Contains("Zebra phase", await ReadServer.CallAsync(client, "work_get", Refs(next), Ct), StringComparison.Ordinal);

        var unique = await ReadServer.CallAsync(client, "work_search", new Dictionary<string, object?> { ["q"] = "zebra run" }, Ct);
        Assert.Contains("Next: work_get(refs:[\"run-z\"])", unique, StringComparison.Ordinal);
    }

    private const string Remote = "https://github.com/r-larch/bare-brief-other";
    private const string Root = "p:/projects/larchsys/bare-brief-other";

    [GeneratedRegex("work_brief\\(scope:\"([^\"]+)\"\\)  ")]
    private static partial Regex Calls();

    [GeneratedRegex("Next: work_get\\(refs:\\[\"([^\"]+)\"\\]\\)")]
    private static partial Regex NextGet();

    /// <summary>A project with one run, created through work_create so its journal dates it.</summary>
    private static async Task ProjectAsync(WriteWorld world, string reference, RepoArg? repo = null)
    {
        var project = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", $"The {reference} project", Ref: reference, Repo: repo)], Ct);
        Assert.False(project.Refused, project.Text);

        var run = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("run", $"The {reference} run", Ref: $"{reference}-run", Parent: reference)], Ct);
        Assert.False(run.Refused, run.Text);

        var task = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", $"A {reference} task", Ref: $"{reference}-task", Parent: $"{reference}-run",
                VerifyCmd: "dotnet test", Criteria: ["it works"])], Ct);
        Assert.False(task.Refused, task.Text);
    }

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private static Dictionary<string, object?> Refs(params string[] refs) => new() { ["refs"] = refs };

    private static Task<string> CreateAsync(McpClient client, object item)
        => ReadServer.CallAsync(client, "work_create", new Dictionary<string, object?> { ["items"] = new[] { item } }, Ct);

    private static List<string> CreatedRefs(string created)
        => [.. created.Split('\n').Skip(1).TakeWhile(l => l.Length > 0).Select(l => l.Trim().Split(' ')[0])];

    private static Task<string> GetAsync(McpClient client, params string[] refs)
        => ReadServer.CallAsync(client, "work_get", Refs(refs), Ct);

    private static async Task<string> ResourceAsync(McpClient client, string uri)
    {
        var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
        return string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }

    private static async Task<string> MintAsync(AppServer server, string name, object repo)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadServer.ControllerToken);

        var response = await client.PostAsync("/api/agent-sessions", AppServer.Json(new { name, role = "orchestrator", repo }), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }
}

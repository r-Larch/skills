using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Import;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The read endpoints of doc 09 §5, asserted as the shape the SPA unit is written against. Every
/// number here comes from a query the MCP tools already use — nothing under <c>/api</c> owns SQL.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AppReadTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_project_switcher_lists_projects_with_the_number_of_runs_under_each()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        var client = await server.SignInAsync(Ct);

        var projects = await AppServer.ReadJsonAsync(client, "/api/projects", Ct);

        var project = Assert.Single(projects.EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(project.GetProperty("ref").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(project.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(project.GetProperty("status").GetString()));
        Assert.Equal(1, project.GetProperty("runCount").GetInt32());
    }

    /// <summary>
    /// doc 09 §5's tree: both numbers on every node, and the disagreement between them marked. A
    /// phase the store calls <c>done</c> over children that are not is the thing this product exists
    /// to surface, so the test seeds exactly that and asserts it survives the serialisation.
    /// </summary>
    [Fact]
    public async Task The_tree_carries_status_and_rollup_and_marks_a_phase_that_disagrees_with_its_children()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.3", status: ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);

        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        var tree = await AppServer.ReadJsonAsync(client, "/api/runs/run-a/tree", Ct);

        Assert.Equal("run-a", tree.GetProperty("ref").GetString());

        var nodes = tree.GetProperty("nodes").EnumerateArray().ToList();
        var phase = Node(nodes, "1");

        Assert.Equal("done", phase.GetProperty("status").GetString());
        Assert.Equal(2, phase.GetProperty("doneCount").GetInt32());
        Assert.Equal(3, phase.GetProperty("totalCount").GetInt32());
        Assert.True(phase.GetProperty("disagrees").GetBoolean());
        Assert.Equal("phase", phase.GetProperty("kind").GetString());
        Assert.Equal(1, phase.GetProperty("depth").GetInt32());

        // A phase whose children are all closed is not a disagreement, and a leaf is not one either
        // — otherwise the glyph would be on every row and would mean nothing.
        var quiet = Node(nodes, "2");
        Assert.Equal(0, quiet.GetProperty("totalCount").GetInt32());
        Assert.False(quiet.GetProperty("disagrees").GetBoolean());

        var leaf = Node(nodes, "1.3");
        Assert.Equal("done", leaf.GetProperty("status").GetString());
        Assert.False(leaf.GetProperty("disagrees").GetBoolean());

        // The root is depth 0 relative to itself, so the client can indent without knowing where in
        // the workspace the run sits.
        Assert.Equal(0, Node(nodes, "run-a").GetProperty("depth").GetInt32());
    }

    /// <summary>
    /// <c>review</c> is unclosed work in flight, so it sits inside <c>totalCount</c>, and it is
    /// broken out so the SPA can say "1 in review" rather than lumping it in with work not started.
    /// The renderer computes the same denominator from the same view — the two agreeing is the
    /// contract, because nothing under <c>/api</c> owns SQL.
    /// </summary>
    [Fact]
    public async Task The_tree_node_carries_review_count_inside_a_total_the_renderer_agrees_with()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        // Task 1.1 is seeded open; one child in each remaining status, plus one that counts nowhere.
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.InProgress);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.3", status: ItemStatus.Blocked);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Review);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.5", status: ItemStatus.Done);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.6", status: ItemStatus.Cancelled);

        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        var tree = await AppServer.ReadJsonAsync(client, "/api/runs/run-a/tree", Ct);
        var phase = Node([.. tree.GetProperty("nodes").EnumerateArray()], "1");

        Assert.Equal(1, phase.GetProperty("reviewCount").GetInt32());
        Assert.Equal(1, phase.GetProperty("doneCount").GetInt32());
        Assert.Equal(5, phase.GetProperty("totalCount").GetInt32());

        var rendered = await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 3, [], true, Ct);
        Assert.Contains("1/5 done", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The watch floor: a lease with the instant it runs out, the attention queue, and the honesty
    /// counts. The lease carries <c>expiresAt</c> and never a remaining-seconds number, because that
    /// number is stale the moment it is serialised and the client is drawing a countdown with it.
    /// </summary>
    [Fact]
    public async Task The_watch_floor_carries_lease_expiry_as_an_instant_and_the_honesty_counts()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var expires = DateTimeOffset.UtcNow.AddMinutes(18);
        await LeaseAsync(world, expires);

        // Closed with nothing independent behind it — the count doc 09 §5 puts on one line.
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);

        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        var watch = await AppServer.ReadJsonAsync(client, "/api/watch", Ct);

        Assert.True(watch.GetProperty("asOf").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        var lease = Assert.Single(watch.GetProperty("workingNow").EnumerateArray());
        Assert.Equal("orchestrator-a", lease.GetProperty("agent").GetString());
        Assert.Equal("1.1", lease.GetProperty("ref").GetString());
        Assert.Equal("The task under test", lease.GetProperty("title").GetString());
        Assert.Equal(expires, lease.GetProperty("expiresAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));

        var honesty = watch.GetProperty("honesty");
        Assert.Equal(1, honesty.GetProperty("doneUnverified").GetInt32());
        Assert.Equal(0, honesty.GetProperty("forced").GetInt32());
        Assert.Equal(0, honesty.GetProperty("ungated").GetInt32());

        var needsYou = watch.GetProperty("needsYou").EnumerateArray().ToList();
        Assert.Contains(needsYou, a => a.GetProperty("rank").GetString() == "done_unverified");
        Assert.All(needsYou, a => Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("call").GetString())));
    }

    /// <summary>
    /// The fourth honesty number, and the only one that is not an accusation: a deferral that
    /// became a tracked follow-up is doc 07 §4 working as designed. It counts what is still open,
    /// so a follow-up that was closed must leave it — otherwise the number only ever grows and
    /// stops meaning anything.
    /// </summary>
    [Fact]
    public async Task The_honesty_strip_counts_open_followups_and_not_closed_ones()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await WriteSeed.ChildAsync(world, world.PhaseId, "F-1", kind: "followup");
        await WriteSeed.ChildAsync(world, world.PhaseId, "F-2", kind: "followup");
        await WriteSeed.ChildAsync(world, world.PhaseId, "F-3", kind: "followup", status: ItemStatus.Done);
        await WriteSeed.ChildAsync(world, world.PhaseId, "F-4", kind: "followup", status: ItemStatus.Cancelled);

        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        var watch = await AppServer.ReadJsonAsync(client, "/api/watch", Ct);

        Assert.Equal(2, watch.GetProperty("honesty").GetProperty("followupsOpen").GetInt32());
    }

    /// <summary>
    /// A first load takes the newest page, which means history can be cut off behind it. Saying so
    /// is the whole job of <c>hasMore</c>: the pane cannot offer what it cannot know exists. This
    /// returned <c>false</c> unconditionally, and on the real corpus that turned a 66-event run
    /// into a 60-event journal with nothing on the page admitting the difference.
    /// </summary>
    [Fact]
    public async Task A_truncated_first_page_says_that_older_events_exist()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        // Real writes through the real writer: the journal is fed from `event`, and seeding items
        // straight through EF produces rows with no history at all.
        var writer = new WorkWriter(
            fixture.DataSource,
            new BoardQueries(fixture.DataSource),
            new Housekeeping(fixture.DataSource));

        for (var n = 0; n < 6; n++)
        {
            var wrote = await writer.NoteAsync(
                world.WorkspaceId,
                new Actor("orchestrator-a", ActorRole.Orchestrator),
                "observation", $"note {n}", "1.1", null, Ct);
            Assert.False(wrote.Refused, wrote.Text);
        }

        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        var full = await AppServer.ReadJsonAsync(client, "/api/runs/run-a/journal?since=0", Ct);
        var total = full.GetProperty("events").GetArrayLength();
        Assert.True(total >= 3, $"fixture needs several events to truncate; it has {total}");

        var page = await AppServer.ReadJsonAsync(client, $"/api/runs/run-a/journal?since=0&limit={total - 1}", Ct);

        Assert.Equal(total - 1, page.GetProperty("events").GetArrayLength());
        Assert.True(page.GetProperty("hasMore").GetBoolean(), "a cut-off first page must admit it");

        // And the page that is NOT cut off must not cry wolf.
        var whole = await AppServer.ReadJsonAsync(client, $"/api/runs/run-a/journal?since=0&limit={total}", Ct);
        Assert.False(whole.GetProperty("hasMore").GetBoolean());

        // Ascending order survives the extra row being trimmed off the old end.
        var seqs = page.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(seqs.OrderBy(s => s), seqs);
    }

    [Fact]
    public async Task An_unknown_project_is_a_404_rather_than_an_empty_watch_floor()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        var response = await client.GetAsync("/api/watch?project=no-such-project", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The SPA shell answers what nothing else claimed, and nothing more. Registered before
    /// <c>/mcp</c> or <c>/api</c> it would answer an agent's tool call with a page of HTML and a
    /// 200, which is the failure that looks like success.
    /// </summary>
    [Fact]
    public async Task The_static_fallback_shadows_neither_api_nor_mcp()
    {
        await using var server = await StartAsync($"fallback-{Guid.CreateVersion7():n}");
        var client = server.NewClient();

        var shell = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal("text/html", shell.Content.Headers.ContentType?.MediaType);

        // No committed placeholder file: wwwroot is the frontend's build output and is gitignored,
        // so the host owns the sentence a clone without Node sees.
        var body = await shell.Content.ReadAsStringAsync(Ct);
        Assert.Contains("not built yet", body, StringComparison.Ordinal);
        Assert.Contains("/api", body, StringComparison.Ordinal);

        // A client-routed path the SPA owns still gets the shell.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/runs/run-a", Ct)).StatusCode);

        // But the two APIs keep their own answers.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/watch", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/no-such-route", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mcp/no-such-route", Ct)).StatusCode);

        var mcp = await client.SendAsync(AppServer.McpInitialize(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);

        var bearer = AppServer.McpInitialize();
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadServer.WorkerToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(bearer, Ct)).StatusCode);
    }

    private static JsonElement Node(IReadOnlyList<JsonElement> nodes, string reference)
        => nodes.FirstOrDefault(n => n.GetProperty("ref").GetString() == reference) is { ValueKind: JsonValueKind.Object } found
            ? found
            : throw new Xunit.Sdk.XunitException(
                $"no node with ref '{reference}'; got "
                + string.Join(", ", nodes.Select(n => n.GetProperty("ref").GetString())));

    private static async Task LeaseAsync(WriteWorld world, DateTimeOffset expires)
    {
        await using var db = world.Fixture.NewContext();

        db.Claims.Add(new Claim
        {
            Id = Guid.CreateVersion7(),
            ItemId = world.TaskId,
            SerializationKey = $"watch-{world.TaskId:n}",
            AgentName = "orchestrator-a",
            Role = ActorRole.Orchestrator,
            AcquiredAt = DateTimeOffset.UtcNow,
            HeartbeatAt = DateTimeOffset.UtcNow,
            ExpiresAt = expires,
        });

        await db.SaveChangesAsync(Ct);
    }

    private Task<ImportedRun> ImportAsync()
        => Corpus.ImportAsync(fixture, "sample-run", $"read-{Guid.CreateVersion7():n}");

    private Task<AppServer> StartAsync(string slug)
        => AppServer.StartAsync(fixture.ConnectionString, slug, Ct);
}

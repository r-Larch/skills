using WorkState.Core.Rendering;
using WorkState.Mcp;
using WorkState.Tests.Import;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// The eight read tools over the real MCP HTTP transport, against a run imported from the
/// committed corpus fixture. Everything asserted here is a contract doc 01 or doc 06 states, not a
/// shape that happened to come out.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ReadToolTests(PostgresFixture fixture)
{
    /// <summary>
    /// The structural write tools. A worker must never see one: it cannot close its own task,
    /// create work, or re-point a dependency, which is why its brief no longer has to ask.
    /// </summary>
    private static readonly string[] WriteTools =
        ["work_create", "work_update", "work_move", "work_link", "work_status", "work_patch", "work_init"];

    /// <summary>
    /// doc 01 §6's worker profile, exactly. Asserted as an exact set rather than a subset: a
    /// "contains" check can never fail, and the whole point of the profile is what is <em>not</em>
    /// in it. A worker reports and verifies; an orchestrator judges and closes.
    /// </summary>
    private static readonly string[] WorkerTools =
        ["work_brief", "work_get", "work_search", "work_report", "work_verify", "work_note"];

    private static readonly string[] OrchestratorOnlyTools =
    [
        "work_next", "work_tree", "work_threads", "work_audit", "work_history",
        "work_status", "work_create", "work_update", "work_move", "work_link",
        "work_assume", "work_finding",
    ];

    [Fact]
    public async Task ToolsList_narrows_to_the_worker_profile_and_widens_for_an_orchestrator()
    {
        await using var server = await StartAsync();

        await using var worker = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        await using var orchestrator = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var workerTools = await WorkToolsAsync(worker);
        var orchestratorTools = await WorkToolsAsync(orchestrator);

        Assert.Equal(WorkerTools.Order(), workerTools.Order());
        Assert.All(OrchestratorOnlyTools, tool => Assert.DoesNotContain(tool, workerTools));
        Assert.All(OrchestratorOnlyTools, tool => Assert.Contains(tool, orchestratorTools));
        Assert.All(WorkerTools, tool => Assert.Contains(tool, orchestratorTools));
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, workerTools));

        Assert.True(
            orchestratorTools.Count > workerTools.Count,
            "an orchestrator token must see strictly more than a worker token");
    }

    [Fact]
    public async Task An_unknown_token_cannot_reach_the_mcp_endpoint_at_all()
    {
        await using var server = await StartAsync();

        var failure = await Record.ExceptionAsync(() => server.ConnectAsync("not-a-real-token", Ct));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task ServerInstructions_reach_the_client_whole_and_inside_their_line_budget()
    {
        await using var server = await StartAsync();
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        Assert.Equal(WorkStateServerInstructions.Text, client.ServerInstructions);

        var lines = WorkStateServerInstructions.Text.Split('\n').Length;
        Assert.True(lines <= WorkStateServerInstructions.LineBudget, $"ServerInstructions is {lines} lines");
    }

    [Fact]
    public async Task work_brief_renders_the_doc_06_shape_with_a_callable_NEXT()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var text = await ReadServer.CallAsync(client, "work_brief", null, Ct);

        Assert.StartsWith("RUN sample-run", text, StringComparison.Ordinal);
        Assert.Contains("\nWHERE  ", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^NEXT   work_[a-z_]+\(.+\)", text);
        Assert.Contains("BINDING (", text, StringComparison.Ordinal);
        Assert.Contains("OPEN ASSUMPTIONS (", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task work_brief_on_an_empty_server_returns_onboarding_that_names_the_next_call()
    {
        await using var server = await StartAsync("no-such-workspace-" + Guid.CreateVersion7().ToString("n"));
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var text = await ReadServer.CallAsync(client, "work_brief", null, Ct);

        Assert.Contains("NEXT   work_create(", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_read_tool_returns_exactly_one_text_block_and_no_structured_content()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        foreach (var (tool, arguments) in EveryCall())
        {
            // CallAsync asserts the contract of doc 01 §2 on every result it unwraps.
            var text = await ReadServer.CallAsync(client, tool, arguments, Ct);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{tool} returned an empty text block");
        }
    }

    [Fact]
    public async Task No_uuid_appears_in_any_tool_rendering()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        foreach (var (tool, arguments) in EveryCall())
        {
            var text = await ReadServer.CallAsync(client, tool, arguments, Ct);
            Assert.DoesNotMatch(RendererTests.Uuid, text);
        }
    }

    [Fact]
    public async Task Every_fixed_work_resource_is_listed_and_readable_and_carries_no_uuid()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var listed = (await client.ListResourcesAsync(cancellationToken: Ct)).Select(r => r.Uri).ToList();

        string[] fixedUris =
            [WorkUris.Workspace, WorkUris.WorkspaceBrief, WorkUris.Conventions, WorkUris.Handoff];

        Assert.All(fixedUris, uri => Assert.Contains(uri, listed));

        foreach (var uri in fixedUris)
        {
            var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
            var body = string.Join("\n", read.Contents
                .OfType<ModelContextProtocol.Protocol.TextResourceContents>()
                .Select(c => c.Text));

            Assert.False(string.IsNullOrWhiteSpace(body), $"{uri} read back empty");
            Assert.DoesNotMatch(RendererTests.Uuid, body);
        }
    }

    [Fact]
    public async Task An_item_uri_a_tool_printed_reads_back_even_though_templates_are_undiscoverable()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var detail = await ReadServer.CallAsync(
            client,
            "work_get",
            new Dictionary<string, object?> { ["refs"] = new[] { "T1.1" } },
            Ct);

        var uri = WorkUris.Item("T1.1");
        Assert.Contains(uri, detail, StringComparison.Ordinal);

        var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
        var body = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(
            Assert.Single(read.Contents)).Text;

        Assert.Contains("T1.1", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// doc 06 §7 and doc 04's third acceptance check, asserted as a number. Measured against a
    /// <b>committed fixture</b>, never the live corpus: those are other projects' working records
    /// and one of them grew mid-build, so a gate against them fails for unrelated reasons.
    /// The bound is noclip's measurement (56 lines, 1063 tokens) plus ~15%: every text is now whole.
    /// </summary>
    [Fact]
    public async Task work_brief_on_the_sample_run_fixture_is_under_1250_tokens_counted_as_chars_over_4()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var text = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        var tokens = (text.Length + 3) / 4;
        var lines = text.Split('\n').Length;

        Assert.True(tokens <= 1250, $"work_brief rendered {tokens} tokens ({text.Length} chars, {lines} lines)");
        Assert.True(lines <= 65, $"work_brief rendered {lines} lines; doc 06 §4 targets 65");
    }

    /// <summary>
    /// doc 04's third budget, split per profile because that is how it is actually paid: a worker
    /// carries six tools for a whole unit and an orchestrator carries the whole surface. Each bound
    /// is the measurement plus about 15% headroom — enough for a description to be reworded, not
    /// enough for one to grow into a specification.
    /// <para>
    /// <b>Measured 2026-09-21, after the acceptance fixes:</b> worker 1890 tokens over 6 tools,
    /// orchestrator and controller 5557 over 18 — up 9 and 19 on the 2026-09-20 figures of 1881 and
    /// 5538, which is one reworded <c>Next:</c> line and one reworded argument.
    /// The cost is flat — the fattest tool is 437 tokens
    /// and the mean is 308 — so there is no fat tool to cut, only 18 tools each documenting every
    /// argument as doc 04 §2 requires. Recorded as a finding rather than met: halving it means
    /// stripping the per-argument descriptions, which is the channel doc 04 §3 leans on hardest.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ReadServer.WorkerToken, 2200)]
    [InlineData(ReadServer.OrchestratorToken, 6400)]
    [InlineData(ReadServer.ControllerToken, 6400)]
    public async Task The_tools_list_payload_stays_inside_the_budget_for_each_profile(string token, int budget)
    {
        await using var server = await StartAsync();
        await using var client = await server.ConnectAsync(token, Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var work = tools.Where(t => t.Name.StartsWith("work_", StringComparison.Ordinal)).ToArray();
        var payload = System.Text.Json.JsonSerializer.Serialize(work.Select(t => t.ProtocolTool));

        var tokens = (payload.Length + 3) / 4;

        // The message names where the budget went, because "over budget" without the three fattest
        // tools is a finding that costs a second run to act on (doc 04 §3, turned on ourselves).
        var worst = string.Join(", ", work
            .Select(t => (t.Name, Tokens: (System.Text.Json.JsonSerializer.Serialize(t.ProtocolTool).Length + 3) / 4))
            .OrderByDescending(t => t.Tokens)
            .Take(3)
            .Select(t => $"{t.Name} {t.Tokens}"));

        Assert.True(
            tokens <= budget,
            $"tools/list for this profile rendered {tokens} tokens ({payload.Length} chars, "
            + $"{work.Length} tools); fattest: {worst}");
    }

    private static IEnumerable<(string Tool, Dictionary<string, object?>? Arguments)> EveryCall() =>
    [
        ("work_brief", null),
        ("work_brief", new Dictionary<string, object?> { ["scope"] = "T1.1", ["for"] = "worker" }),
        ("work_next", null),
        ("work_tree", null),
        ("work_get", new Dictionary<string, object?> { ["refs"] = new[] { "T1.1", "phase-1" }, ["include"] = "deps,body" }),
        ("work_search", new Dictionary<string, object?> { ["q"] = "token" }),
        ("work_threads", null),
        ("work_audit", null),
        ("work_history", null),
    ];

    private static async Task<IReadOnlyList<string>> WorkToolsAsync(ModelContextProtocol.Client.McpClient client)
    {
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        return [.. tools.Select(t => t.Name).Where(n => n.StartsWith("work_", StringComparison.Ordinal))];
    }

    private Task<ImportedRun> ImportAsync()
        => Corpus.ImportAsync(fixture, "sample-run", $"read-{Guid.CreateVersion7():n}");

    private Task<ReadServer> StartAsync(string? workspaceSlug = null)
        => ReadServer.StartAsync(fixture.ConnectionString, workspaceSlug, Ct);
}

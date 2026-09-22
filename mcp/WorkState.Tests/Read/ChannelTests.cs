using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using WorkState.Mcp;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// doc 04's four channels, asserted rather than reviewed. Three of them are strings the server ships
/// — <c>ServerInstructions</c>, the tool descriptions, the refusals — and a string is assertable, so
/// an audit that was a checklist someone ticked becomes a build-failing gate.
/// </summary>
[Collection(Store.StoreCollection.Name)]
public sealed partial class ChannelTests(Store.PostgresFixture fixture)
{
    /// <summary>
    /// doc 04 §2's two orientation points. Everything else has to reach one of them in two hops or
    /// it is somewhere an agent gets lost.
    /// </summary>
    private static readonly string[] Orientation = ["work_next", "work_brief"];

    /// <summary>
    /// The tools doc 04 §2 declares terminal: <c>work_note</c> and <c>work_constrain</c> are side
    /// effects, and saying so is what stops an agent hunting for a follow-up that does not exist.
    /// Listed here rather than inferred, so adding a dead end is a deliberate edit to this array.
    /// </summary>
    private static readonly string[] Terminal = ["work_note", "work_constrain"];

    [GeneratedRegex(@"\bwork_[a-z_]+", RegexOptions.Compiled)]
    private static partial Regex ToolMention();

    /// <summary>
    /// The shipped string is <c>docs/workstate/05-server-instructions.md</c> <b>verbatim</b>. The
    /// document is the source and the constant is a copy, and nothing but a test keeps a copy honest
    /// — U3 and U4 both had the opportunity to drift it silently.
    /// </summary>
    [Fact]
    public void ServerInstructions_are_doc_05_verbatim()
    {
        var document = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "05-server-instructions.md"))
            .Replace("\r\n", "\n");

        var open = document.IndexOf("```text\n", StringComparison.Ordinal);
        Assert.True(open >= 0, "doc 05 no longer carries a ```text fence");

        var start = open + "```text\n".Length;
        var end = document.IndexOf("\n```", start, StringComparison.Ordinal);
        Assert.True(end > start, "doc 05's ```text fence is unclosed");

        Assert.Equal(document[start..end], WorkStateServerInstructions.Text.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// doc 04 §2, property 1. A <c>Next:</c> naming a tool that does not exist — <c>work_patch</c>,
    /// say, which is designed but unshipped — is a dead end that reads as a live one.
    /// </summary>
    [Fact]
    public async Task Every_Next_line_names_a_tool_that_exists()
    {
        var graph = await GraphAsync();

        foreach (var (tool, next) in graph)
        {
            Assert.All(next, named => Assert.True(
                graph.ContainsKey(named),
                $"{tool} names {named}, which this server does not expose"));
        }
    }

    /// <summary>
    /// doc 04 §2, property 2. Two hops, because the third is where an agent stops following and
    /// starts guessing.
    /// </summary>
    [Fact]
    public async Task Every_tool_reaches_work_next_or_work_brief_within_two_hops()
    {
        var graph = await GraphAsync();

        foreach (var tool in graph.Keys)
        {
            if (Terminal.Contains(tool))
            {
                Assert.Empty(graph[tool]);
                continue;
            }

            Assert.True(
                Hops(graph, tool) <= 2,
                $"{tool} is {Hops(graph, tool)} hops from work_next or work_brief; "
                + $"its Next: names {string.Join(", ", graph[tool])}");
        }
    }

    /// <summary>
    /// doc 04 §2, property 3 — *"a report is a claim, not evidence"*, the sentence that arrives at
    /// the exact moment an agent is about to trust a self-report. Asserted in those words.
    /// </summary>
    [Fact]
    public async Task work_report_points_at_work_verify_in_those_words()
    {
        var descriptions = await DescriptionsAsync();

        Assert.Contains("work_verify", descriptions["work_report"], StringComparison.Ordinal);
        Assert.Contains(
            "a report is a claim, not evidence",
            descriptions["work_report"],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// doc 04 §2: a description answers <em>what it does</em>, <em>when to reach for it</em>, and
    /// <em>what to call next</em>. Only the third is mechanically checkable, so that is what this
    /// checks — on every tool, including any added later.
    /// </summary>
    [Fact]
    public async Task Every_tool_description_carries_a_Next_line()
    {
        var descriptions = await DescriptionsAsync();

        Assert.NotEmpty(descriptions);
        Assert.All(descriptions, entry => Assert.Contains(
            "Next:",
            entry.Value,
            StringComparison.Ordinal));
    }

    /// <summary>
    /// The overlap doc 04 §1 forbids one level down: both the tool description and the
    /// <c>inputSchema</c> property descriptions ship in <c>tools/list</c>, so a description that
    /// restates an argument is paid for twice. A measurement, not a gate — the bound is loose enough
    /// that only real duplication trips it.
    /// <para>
    /// <b>Measured 2026-09-21, 18 tools: mean 13%, median 11%, six tools at 0%.</b> The one outlier
    /// is <c>work_verify</c> at 47%, and it is not duplication: its description exists to say that
    /// <c>run_by</c> comes from the token rather than from the argument, which means naming the
    /// argument. So the 5,557-token <c>tools/list</c> payload is the price of the channel, not slack
    /// in it — there is nothing to cut that is not doc 04 §2's third question.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tool_description_does_not_restate_its_own_input_schema()
    {
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, null, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var overlap = new List<string>();

        foreach (var tool in await WorkToolsAsync(client))
        {
            var described = Words(tool.Description ?? string.Empty);
            var schema = Words(tool.ProtocolTool.InputSchema.ToString());
            if (described.Count == 0)
            {
                continue;
            }

            var shared = described.Intersect(schema).Count();
            var share = shared * 100 / described.Count;
            overlap.Add($"{tool.Name} {share}%");

            Assert.True(share <= 60, $"{tool.Name} restates {share}% of its own schema");
        }

        Assert.NotEmpty(overlap);
    }

    /// <summary>
    /// Only distinctive words count. Every description and every schema shares "the", "a" and "work",
    /// so counting those would measure English rather than duplication.
    /// </summary>
    private static HashSet<string> Words(string text)
        => [.. Regex.Matches(text.ToLowerInvariant(), @"[a-z_]{5,}")
                 .Select(m => m.Value)
                 .Where(w => w is not ("description" or "string" or "properties" or "boolean"
                                       or "integer" or "object" or "array" or "which" or "there"))];

    private static int Hops(IReadOnlyDictionary<string, IReadOnlyList<string>> graph, string from)
    {
        var seen = new HashSet<string> { from };
        var frontier = new List<string> { from };

        for (var hop = 0; hop <= 2; hop++)
        {
            if (frontier.Any(Orientation.Contains))
            {
                return hop;
            }

            frontier = [.. frontier
                .SelectMany(t => graph.TryGetValue(t, out var next) ? next : [])
                .Where(seen.Add)];
        }

        return int.MaxValue;
    }

    /// <summary>
    /// The workflow graph of doc 04 §2, read back off the wire: the tools an agent actually receives,
    /// and the tools each one's <c>Next:</c> line points it at. Parsed from <c>Next:</c> onwards, so
    /// a tool named in the body ("not to decide what to do next, which is work_next's job") is prose
    /// rather than an edge.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> GraphAsync()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (name, description) in await DescriptionsAsync())
        {
            var at = description.IndexOf("Next:", StringComparison.Ordinal);
            var tail = at < 0 ? string.Empty : description[at..];

            graph[name] = [.. ToolMention()
                .Matches(tail)
                .Select(m => m.Value)
                .Where(m => m != name)
                .Distinct()];
        }

        return graph;
    }

    private async Task<Dictionary<string, string>> DescriptionsAsync()
    {
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, null, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var tools = await WorkToolsAsync(client);
        return tools.ToDictionary(t => t.Name, t => t.Description ?? string.Empty, StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<McpClientTool>> WorkToolsAsync(McpClient client)
    {
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        return [.. tools.Where(t => t.Name.StartsWith("work_", StringComparison.Ordinal))];
    }
}

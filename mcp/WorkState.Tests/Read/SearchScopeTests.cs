using System.Text.RegularExpressions;
using Dapper;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// search-scope. work_search found items only, so a rule, an assumption or a note could be read only
/// by someone who already knew where it sat; and it searched the whole workspace, with no way to ask
/// "under this run". Every route a hit prints is called back here, because a route that does not
/// resolve is a dead end printed as if it were a way forward.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class SearchScopeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_note_a_constraint_and_an_assumption_are_found_and_each_printed_route_opens_it()
    {
        var world = await SeedAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var found = await ReadServer.CallAsync(
            client, "work_search", new Dictionary<string, object?> { ["q"] = "quokka", ["limit"] = 20 }, Ct);

        var cases = new (string Row, string Kind, string Opened)[]
        {
            ("cache", "assumption · open", "the quokka cache survives restarts"),
            ("ledger", "constraint · domain_rule", "the quokka ledger is append-only"),
            ("offline", "constraint · stack_fact", "quokka builds run offline"),
            ("migration", "note · carry_forward", "quokka migration finished at 4f2a"),
        };

        foreach (var (row, kind, opened) in cases)
        {
            var route = RouteOf(found, row, kind);
            var text = await FollowAsync(client, route);
            var unwrapped = Regex.Replace(text, @"\s+", " ");
            Assert.True(unwrapped.Contains(opened, StringComparison.Ordinal), $"{route} did not print \"{opened}\":\n{text}");
        }

        Assert.Contains("→ work_get(refs:[\"run-a/A1\"])", found, StringComparison.Ordinal);
        Assert.Contains("→ read work://item/1.1/brief", found, StringComparison.Ordinal);
        Assert.Contains($"→ read {WorkUris.Conventions}", found, StringComparison.Ordinal);
        Assert.Contains("→ work_history(scope:\"1.1\")", found, StringComparison.Ordinal);
        Assert.Contains("Feed the quokka", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_keeps_only_hits_inside_its_subtree()
    {
        var world = await SeedAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var task = await SearchAsync(client, "1.1");
        Assert.StartsWith("SEARCH \"quokka\" under 1.1 — 4 hits", task, StringComparison.Ordinal);
        Assert.Contains("cache", task, StringComparison.Ordinal);
        Assert.Contains("ledger", task, StringComparison.Ordinal);
        Assert.Contains("migration", task, StringComparison.Ordinal);
        Assert.Contains("first draft", task, StringComparison.Ordinal);
        Assert.DoesNotContain("Feed the quokka", task, StringComparison.Ordinal);
        Assert.DoesNotContain("offline", task, StringComparison.Ordinal);
        Assert.DoesNotContain("far side", task, StringComparison.Ordinal);
        Assert.DoesNotContain("fed twice", task, StringComparison.Ordinal);

        var other = await SearchAsync(client, "run-b");
        Assert.StartsWith("SEARCH \"quokka\" under run-b — 2 hits", other, StringComparison.Ordinal);
        Assert.Contains("far side", other, StringComparison.Ordinal);
        Assert.Contains("→ work_get(refs:[\"run-b/A1\"])", other, StringComparison.Ordinal);
        Assert.DoesNotContain("run-a", other, StringComparison.Ordinal);
        Assert.DoesNotContain("1.1", other, StringComparison.Ordinal);
        Assert.DoesNotContain("offline", other, StringComparison.Ordinal);

        var whole = await SearchAsync(client, null);
        Assert.StartsWith("SEARCH \"quokka\" — 8 hits", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// The items-only search, as it stood before this change — its SQL and its row format — is the
    /// expectation for a search whose hits are all items, with non-matching threads beside them.
    /// </summary>
    [Fact]
    public async Task Without_a_scope_an_items_only_search_prints_what_it_printed_before()
    {
        var world = await SeedAsync();
        await AddItemAsync(world, world.PhaseId, "1.3", 3m, "Wire the frontier query",
            "The orchestrator needs a cheap answer to what is frontier-ready right now.");
        await AddItemAsync(world, world.PhaseId, "1.4", 4m, "Measure the frontier", null);

        var before = await BeforeAsync(world, "frontier");
        var after = await world.Reader.SearchAsync(world.WorkspaceId, "frontier", 10, Ct);

        Assert.Equal(before, after);
        Assert.Contains("1.3", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_scope_is_refused_and_searches_nothing()
    {
        var world = await SeedAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var text = await SearchAsync(client, "no-such-run");

        Assert.Contains("work_search — no item has ref \"no-such-run\".", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SEARCH", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ambiguous_scope_is_refused_with_its_candidates()
    {
        var world = await SeedAsync();
        await WriteSeed.ChildAsync(world, (await world.ItemAsync("run-b")).Id, "1", kind: "phase");
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var text = await SearchAsync(client, "1");

        Assert.Contains("work_search — \"1\" exists in 2 runs.", text, StringComparison.Ordinal);
        Assert.Contains("work_search(scope:\"run-a/1\", q:\"quokka\", limit:20)", text, StringComparison.Ordinal);
        Assert.Contains("work_search(scope:\"run-b/1\", q:\"quokka\", limit:20)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SEARCH", text, StringComparison.Ordinal);

        var printed = Regex.Matches(text, """work_search\(scope:"(?<scope>[^"]+)", q:"(?<q>[^"]+)", limit:(?<limit>\d+)\)""");
        Assert.Equal(2, printed.Count);
        foreach (Match call in printed)
        {
            var again = await ReadServer.CallAsync(
                client,
                "work_search",
                new Dictionary<string, object?>
                {
                    ["scope"] = call.Groups["scope"].Value,
                    ["q"] = call.Groups["q"].Value,
                    ["limit"] = int.Parse(call.Groups["limit"].Value),
                },
                Ct);
            Assert.StartsWith($"SEARCH \"quokka\" under {call.Groups["scope"].Value}", again, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// tool-budget. A note hit routed to work_history, which a worker token cannot call; a worker gets
    /// the note whole under its row and work_get on the item it is on, and every route still opens.
    /// </summary>
    [Fact]
    public async Task A_worker_gets_a_note_whole_and_a_route_it_can_call()
    {
        var world = await SeedAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var found = await ReadServer.CallAsync(
            client, "work_search", new Dictionary<string, object?> { ["q"] = "quokka", ["limit"] = 20 }, Ct);

        Assert.DoesNotContain("work_history", found, StringComparison.Ordinal);

        var route = RouteOf(found, "migration", "note · carry_forward");
        Assert.Equal("work_get(refs:[\"1.1\"])", route);
        var lines = found.Split('\n');
        var row = Array.FindIndex(lines, l => l.Contains("migration", StringComparison.Ordinal));
        Assert.Equal($"{new string(' ', 20)}note: quokka migration finished at 4f2a", lines[row + 2]);

        foreach (var line in lines.Where(l => l.TrimStart().StartsWith("→ work_", StringComparison.Ordinal)))
        {
            var opened = await FollowAsync(client, line.Trim()["→ ".Length..]);
            Assert.DoesNotContain("REFUSED", opened, StringComparison.Ordinal);
            Assert.DoesNotContain("✗", opened.Split('\n')[0], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// tool-budget. An assumption on no item printed its bare A-ref, which a run's own A1 makes
    /// ambiguous, and no qualified form existed. <c>workspace/A1</c> is that form, on every surface
    /// that resolves an A-ref.
    /// </summary>
    [Fact]
    public async Task A_workspace_assumption_is_printed_qualified_and_the_printed_ref_resolves_back()
    {
        // assumption_ref_unique is NULLS NOT DISTINCT on (ref_scope_id, ref), so a workspace-wide ref
        // is unique across every workspace in the shared fixture database: A1 belongs to AmbiguousRefTests.
        const string number = "A57";
        var world = await SeedAsync();
        await using (var db = fixture.NewContext())
        {
            foreach (var (scope, statement) in new (Guid?, string)[]
                     {
                         (world.RunId, "the quokka sanctuary closes in winter"),
                         (null, "the quokka sanctuary is open all year"),
                     })
            {
                db.Assumptions.Add(new Core.Domain.Assumption
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = world.WorkspaceId,
                    ScopeItemId = scope,
                    Ref = number,
                    Statement = statement,
                    Evidence = Core.Domain.EvidenceLevel.Reasoned,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var found = await ReadServer.CallAsync(
            client, "work_search", new Dictionary<string, object?> { ["q"] = "quokka sanctuary open" }, Ct);
        var route = RouteOf(found, $"workspace/{number}", "assumption · open");
        Assert.Equal($"work_get(refs:[\"workspace/{number}\"])", route);

        var opened = await FollowAsync(client, route);
        Assert.Contains("the quokka sanctuary is open all year", Regex.Replace(opened, @"\s+", " "), StringComparison.Ordinal);
        Assert.Contains($"Next: work_assume(ref:\"workspace/{number}\", resolve:\"confirmed\"", opened, StringComparison.Ordinal);

        var bare = await ReadServer.CallAsync(client, "work_get", new Dictionary<string, object?> { ["refs"] = new[] { number } }, Ct);
        Assert.Contains($"\"workspace/{number}\"", bare, StringComparison.Ordinal);
        Assert.Contains($"\"run-a/{number}\"", bare, StringComparison.Ordinal);

        var resolved = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, $"workspace/{number}", null, "settled", null, null, "confirmed", null, Ct);
        Assert.False(resolved.Refused, resolved.Text);
        Assert.StartsWith($"OK  {number}  resolved confirmed", resolved.Text, StringComparison.Ordinal);

        await using var check = fixture.NewContext();
        Assert.Equal(
            Core.Domain.AssumptionState.Confirmed,
            check.Assumptions.Single(a => a.WorkspaceId == world.WorkspaceId && a.ScopeItemId == null).State);
    }

    /// <summary>
    /// Two runs, each with an <c>A1</c>, so a bare A-ref is ambiguous and only the qualified one opens
    /// the assumption. Run-a: an item, a scoped rule, an assumption and two notes on 1.1; run-b: one of
    /// each thread; and one workspace rule.
    /// </summary>
    private async Task<WriteWorld> SeedAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        var writer = world.Writer;
        var me = WriteSeed.Orchestrator;

        await AddItemAsync(world, world.PhaseId, "1.2", 2m, "Feed the quokka", null);

        Ok(await writer.ConstrainAsync(world.WorkspaceId, me, null,
            new ConstrainArgs(Kind: "domain_rule", Text: "the quokka ledger is append-only", Scope: "1.1"), Ct));
        Ok(await writer.ConstrainAsync(world.WorkspaceId, me, null,
            new ConstrainArgs(Kind: "stack_fact", Text: "quokka builds run offline"), Ct));

        Ok(await writer.AssumeAsync(world.WorkspaceId, me, "1.1", "the quokka cache survives restarts",
            "reasoned", "medium", null, null, null, Ct));
        Ok(await writer.AssumeAsync(world.WorkspaceId, me, "9.1", "the quokka is fed twice a day",
            "reasoned", "medium", null, null, null, Ct));

        Ok(await writer.NoteAsync(world.WorkspaceId, me, "carry_forward", "quokka migration finished at 4f2a", "1.1", null, Ct));
        Ok(await writer.NoteAsync(world.WorkspaceId, me, "observation", "the quokka first draft was wrong", null, "1.1", Ct));
        Ok(await writer.NoteAsync(world.WorkspaceId, me, "observation", "the quokka on the far side", "9.1", null, Ct));

        return world;
    }

    private static void Ok(WriteResult result) => Assert.False(result.Refused, result.Text);

    private async Task AddItemAsync(WriteWorld world, Guid parent, string reference, decimal seq, string title, string? intent)
    {
        await using var db = fixture.NewContext();
        var item = Seed.NewItem(world.WorkspaceId, parent, "task", reference, seq, title);
        item.Intent = intent;
        db.Items.Add(item);
        await db.SaveChangesAsync(Ct);
    }

    private static Task<string> SearchAsync(McpClient client, string? scope)
        => ReadServer.CallAsync(
            client,
            "work_search",
            new Dictionary<string, object?> { ["q"] = "quokka", ["limit"] = 20, ["scope"] = scope },
            Ct);

    /// <summary>The route printed on the line under the hit whose row holds <paramref name="word"/>.</summary>
    private static string RouteOf(string output, string word, string kind)
    {
        var lines = output.Split('\n');
        var at = Array.FindIndex(lines, l => l.Contains(word, StringComparison.Ordinal));
        Assert.True(at >= 0 && at + 1 < lines.Length, $"no row for \"{word}\":\n{output}");
        Assert.Contains(kind, lines[at], StringComparison.Ordinal);

        var route = lines[at + 1].Trim();
        Assert.StartsWith("→ ", route, StringComparison.Ordinal);
        return route["→ ".Length..];
    }

    private static async Task<string> FollowAsync(McpClient client, string route)
    {
        if (route.StartsWith("read ", StringComparison.Ordinal))
        {
            var read = await client.ReadResourceAsync(route["read ".Length..], cancellationToken: Ct);
            return string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
        }

        var call = Call().Match(route);
        Assert.True(call.Success, $"not a callable route: {route}");

        var arguments = call.Groups["arg"].Success
            ? new Dictionary<string, object?>
            {
                [call.Groups["arg"].Value] = call.Groups["list"].Success
                    ? new[] { call.Groups["value"].Value }
                    : call.Groups["value"].Value,
            }
            : null;

        return await ReadServer.CallAsync(client, call.Groups["tool"].Value, arguments, Ct);
    }

    [GeneratedRegex("""^(?<tool>work_\w+)\((?:(?<arg>\w+):(?<list>\[)?"(?<value>[^"]*)"\]?)?\)$""")]
    private static partial Regex Call();

    private async Task<string> BeforeAsync(WriteWorld world, string query)
    {
        const string sql =
            """
            WITH q AS (SELECT websearch_to_tsquery('english', @query) AS tsq)
            SELECT i.id AS "Id", i.ref AS "Ref", i.title AS "Title",
                   ts_headline('english', coalesce(i.intent, i.title), q.tsq,
                               'MaxFragments=1,MaxWords=18,MinWords=5') AS "Snippet"
            FROM   item i, q
            WHERE  i.workspace_id = @workspace AND i.search @@ q.tsq
            ORDER BY ts_rank(i.search, q.tsq) DESC, "Ref"
            LIMIT 10;
            """;

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var hits = (await connection.QueryAsync<(Guid Id, string Ref, string Title, string Snippet)>(
            sql, new { workspace = world.WorkspaceId, query })).AsList();

        var board = new BoardQueries(fixture.DataSource);
        var lines = new List<string> { $"SEARCH \"{query}\" — {hits.Count} hits", string.Empty };
        var printed = new List<string>();
        foreach (var hit in hits)
        {
            printed.Add(await board.PrintableRefAsync(world.WorkspaceId, hit.Id, hit.Ref, Ct));
        }

        lines.AddRange(hits.Select((hit, i) =>
            $"{Glyph.Pad(Glyph.Flat(printed[i]), 18)}  {Glyph.Pad(Glyph.Row(hit.Title, 30), 30)}  {Glyph.Row(hit.Snippet, 40)}"));
        lines.Add(string.Empty);
        lines.Add($"Next: work_get(refs:[\"{printed[0]}\"]) on a hit.");
        return string.Join("\n", lines);
    }
}

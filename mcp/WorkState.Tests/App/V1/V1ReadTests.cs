using System.Net;
using System.Text.Json;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.V1.V1Http;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>One test per doc 10 endpoint, each against <see cref="V1World"/> and read with a reader token.</summary>
[Collection(StoreCollection.Name)]
public sealed class V1ReadTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_overview_carries_projects_runs_working_now_inbox_and_honesty()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);

        var overview = await GetAsync(Reader(server), "/api/v1/overview");

        var project = Assert.Single(Array(overview, "projects"));
        Assert.Equal(v1.World.ProjectId, project.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("project", project.GetProperty("item").GetProperty("kind").GetString());

        // Leaf kinds only: tasks 1.1–1.7 count, phases and the finding F-1 do not.
        var rollup = project.GetProperty("rollup");
        Assert.Equal(7, rollup.GetProperty("total").GetInt32());
        Assert.Equal(2, rollup.GetProperty("done").GetInt32());
        Assert.Equal(1, rollup.GetProperty("inProgress").GetInt32());
        Assert.Equal(2, rollup.GetProperty("blocked").GetInt32());
        Assert.Equal(2, rollup.GetProperty("open").GetInt32());

        var run = Assert.Single(Array(project, "runs"));
        Assert.Equal(v1.World.RunId, run.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(1, run.GetProperty("activeClaims").GetInt32());
        Assert.True(run.GetProperty("lastVerification").GetProperty("passed").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("lastEventAt").ValueKind);
        Assert.Equal(overview.GetProperty("inboxCount").GetInt32(), run.GetProperty("inboxCount").GetInt32());

        var claim = Assert.Single(Array(overview, "workingNow"));
        Assert.Equal("1.1", claim.GetProperty("item").GetProperty("ref").GetString());
        Assert.Equal(V1World.SessionName, claim.GetProperty("session").GetProperty("name").GetString());
        Assert.Equal(v1.Tree, claim.GetProperty("tree").GetString());
        Assert.Equal("run-a", claim.GetProperty("run").GetProperty("ref").GetString());
        Assert.False(claim.GetProperty("expired").GetBoolean());
        Assert.True(claim.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        var honesty = overview.GetProperty("honesty");
        Assert.Equal(1, honesty.GetProperty("doneUnverified").GetInt32());
        Assert.Equal(0, honesty.GetProperty("forced").GetInt32());
    }

    /// <summary>The honesty counts are the attention block's and work_audit's: a deferral is not unverified.</summary>
    [Fact]
    public async Task An_obsolete_item_is_not_done_unverified_anywhere()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var audit = await v1.World.Reader.AuditAsync(await v1.World.ScopeAsync("run-a"), Ct);
        Assert.Contains("1.2", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("1.7", audit, StringComparison.Ordinal);

        var overview = await GetAsync(reader, "/api/v1/overview");
        Assert.Equal(1, overview.GetProperty("honesty").GetProperty("doneUnverified").GetInt32());

        var board = await GetAsync(reader, $"/api/v1/runs/{v1.World.RunId}/board");
        var flagged = Array(board, "rows")
            .Where(r => r.GetProperty("flags").EnumerateArray().Any(f => f.GetString() == "done_unverified"))
            .Select(r => r.GetProperty("item").GetProperty("ref").GetString())
            .ToList();
        Assert.Equal(["1.2"], flagged);

        var inbox = await GetAsync(reader, "/api/v1/inbox");
        var unverified = Array(inbox, "rows").Where(r => r.GetProperty("kind").GetString() == "done_unverified").ToList();
        Assert.Equal(v1.Unverified, Assert.Single(unverified).GetProperty("item").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_board_is_flat_pre_order_and_carries_claim_criteria_commit_blockers_and_flags()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);

        var board = await GetAsync(Reader(server), $"/api/v1/runs/{v1.World.RunId}/board");

        Assert.Equal(v1.World.RunId, board.GetProperty("run").GetProperty("id").GetGuid());
        Assert.Equal(v1.World.ProjectId, board.GetProperty("project").GetProperty("id").GetGuid());

        var rows = Array(board, "rows");
        var refs = rows.Select(r => r.GetProperty("item").GetProperty("ref").GetString()).ToList();
        Assert.Equal(["run-a", "1", "1.1", "F-1", "1.2", "1.4", "1.3", "1.5", "1.6", "1.7", "2"], refs);

        // A parent precedes its children, and depth is relative to the board's own root.
        Assert.Equal(0, rows[0].GetProperty("depth").GetInt32());
        var phase = ByRef(rows, "1");
        Assert.Equal(1, phase.GetProperty("depth").GetInt32());
        Assert.True(phase.GetProperty("hasChildren").GetBoolean());
        Assert.Equal(v1.World.RunId, phase.GetProperty("parentId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, phase.GetProperty("rollup").ValueKind);

        var task = ByRef(rows, "1.1");
        Assert.Equal("in_progress", task.GetProperty("item").GetProperty("status").GetString());
        Assert.Equal(1, task.GetProperty("criteria").GetProperty("checked").GetInt32());
        Assert.Equal(2, task.GetProperty("criteria").GetProperty("total").GetInt32());
        Assert.Equal(V1World.SessionName, task.GetProperty("claim").GetProperty("session").GetProperty("name").GetString());
        Assert.Equal(v1.Tree, task.GetProperty("claim").GetProperty("tree").GetString());
        Assert.Equal("a439808f00", task.GetProperty("commit").GetString());
        Assert.Equal("orchestrator", task.GetProperty("verification").GetProperty("runBy").GetString());
        Assert.Contains("unconsumed_notes", task.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));

        var waiting = ByRef(rows, "1.3");
        Assert.Equal("1.4", Assert.Single(waiting.GetProperty("blockedBy").EnumerateArray()).GetProperty("ref").GetString());

        var obsolete = ByRef(rows, "1.7");
        Assert.Equal("obsolete", obsolete.GetProperty("resolution").GetString());
        Assert.Equal(JsonValueKind.Null, ByRef(rows, "1.2").GetProperty("rollup").ValueKind);
    }

    [Fact]
    public async Task The_item_page_splits_what_was_reported_from_what_was_verified()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);

        var item = await GetAsync(Reader(server), $"/api/v1/items/{v1.World.TaskId}");

        Assert.Equal("1.1", item.GetProperty("item").GetProperty("ref").GetString());
        Assert.Equal(
            [v1.World.ProjectId, v1.World.RunId, v1.World.PhaseId],
            Array(item, "path").Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal("dotnet test --filter Guard", item.GetProperty("verifyCmd").GetString());

        var criteria = Array(item, "criteria");
        Assert.Equal(["c1", "c2"], criteria.Select(c => c.GetProperty("id").GetString()));
        Assert.True(criteria[0].GetProperty("checked").GetBoolean());

        var report = Assert.Single(Array(item, "reports"));
        Assert.Equal("done", report.GetProperty("status").GetString());
        Assert.Equal(["a.cs", "b.cs"], report.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("dotnet test → Passed! 91", report.GetProperty("verify").GetString());
        Assert.Equal("broke line 4, went red", report.GetProperty("redCheck").GetString());
        Assert.Equal(V1World.SessionName, report.GetProperty("session").GetProperty("name").GetString());

        var verification = Assert.Single(Array(item, "verifications"));
        Assert.True(verification.GetProperty("passed").GetBoolean());
        Assert.Equal("Passed! Failed: 0, Passed: 91", verification.GetProperty("resultLine").GetString());
        Assert.Equal("orchestrator", verification.GetProperty("runBy").GetString());
        Assert.Equal(91, verification.GetProperty("counts").GetProperty("passed").GetInt32());
        Assert.Equal("red at line 4", verification.GetProperty("redCheck").GetString());

        Assert.Equal("a439808f00", Assert.Single(Array(item, "artifacts")).GetProperty("value").GetString());
        Assert.Equal(V1World.SessionName, item.GetProperty("claim").GetProperty("session").GetProperty("name").GetString());

        var finding = Assert.Single(Array(item, "findings"));
        Assert.Equal(v1.Blocker, finding.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("blocker", finding.GetProperty("severity").GetString());

        var note = Assert.Single(Array(item, "notes"));
        Assert.Equal("decision", note.GetProperty("kind").GetString());
        Assert.Equal("1.1", note.GetProperty("to").GetProperty("ref").GetString());

        Assert.Contains(Array(item, "children"), c => c.GetProperty("item").GetProperty("ref").GetString() == "F-1");

        var events = Array(item, "events");
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(v1.World.TaskId, e.GetProperty("item").GetProperty("id").GetGuid()));
        Assert.Equal(events.Select(e => e.GetProperty("seq").GetInt64()).Order(), events.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.Contains(events, e => e.GetProperty("session").ValueKind == JsonValueKind.Object);
        Assert.False(item.GetProperty("eventsHasMore").GetBoolean());

        var links = item.GetProperty("links");
        Assert.Empty(links.GetProperty("in").EnumerateArray());
    }

    [Fact]
    public async Task The_item_page_lists_links_both_ways_and_assumptions()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var writer = v1.World.Writer;
        var assumed = await writer.AssumeAsync(
            v1.World.WorkspaceId, Write.WriteSeed.Orchestrator, "1.3", "the gate closes first", "reasoned", "low", "1.4", null, null, Ct);
        Assert.False(assumed.Refused, assumed.Text);

        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var waiting = await GetAsync(reader, $"/api/v1/items/{v1.WaitsOnWork}");
        var into = Assert.Single(waiting.GetProperty("links").GetProperty("in").EnumerateArray());
        Assert.Equal("blocks", into.GetProperty("type").GetString());
        Assert.Equal("1.4", into.GetProperty("item").GetProperty("ref").GetString());

        var assumption = Assert.Single(Array(waiting, "assumptions"));
        Assert.Equal("the gate closes first", assumption.GetProperty("text").GetString());
        Assert.Equal("open", assumption.GetProperty("status").GetString());
        Assert.Equal("1.4", assumption.GetProperty("revisitWhen").GetProperty("ref").GetString());
    }

    [Fact]
    public async Task An_unknown_item_or_one_from_another_workspace_is_a_404()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var other = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        foreach (var id in (Guid[])[Guid.CreateVersion7(), other.World.TaskId])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/items/{id}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/runs/{id}/board", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task The_brief_is_the_verbatim_work_brief_as_text()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var response = await reader.GetAsync($"/api/v1/items/{v1.World.RunId}/brief", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var expected = await v1.World.Reader.BriefSessionAsync(
            await v1.World.ScopeAsync("run-a"), "resume", Write.WriteSeed.Keeper(fixture), Ct);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reader.GetAsync($"/api/v1/items/{v1.World.RunId}/brief?for=nonsense", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_inbox_lists_what_waits_on_a_person_and_not_what_waits_on_work()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);

        var inbox = await GetAsync(Reader(server), $"/api/v1/inbox?project={v1.World.ProjectId}");
        var rows = Array(inbox, "rows");

        var blocked = Assert.Single(rows, r => r.GetProperty("kind").GetString() == "blocked");
        Assert.Equal(v1.BlockedOnPerson, blocked.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal($"blocked:{v1.BlockedOnPerson}", blocked.GetProperty("key").GetString());
        Assert.Equal("work_status(ref:\"1.6\", to:\"in_progress\")", blocked.GetProperty("call").GetString());
        Assert.Equal("run-a", blocked.GetProperty("run").GetProperty("ref").GetString());

        // Waiting on a blocks edge — open or marked blocked — is waiting on work.
        Assert.DoesNotContain(rows, r => r.GetProperty("item").GetProperty("id").GetGuid() == v1.WaitsOnWork);
        Assert.DoesNotContain(rows, r => r.GetProperty("item").GetProperty("id").GetGuid() == v1.BlockedBehindWork);

        var finding = Assert.Single(rows, r => r.GetProperty("kind").GetString() == "blocker_finding");
        Assert.Equal(v1.Blocker, finding.GetProperty("item").GetProperty("id").GetGuid());

        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "done_unverified");
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "unconsumed_note");

        // Newest first, and every key unique and of the documented shape.
        var since = rows.Select(r => r.GetProperty("since").GetDateTimeOffset()).ToList();
        Assert.Equal(since.OrderDescending(), since);
        Assert.Equal(rows.Count, rows.Select(r => r.GetProperty("key").GetString()).Distinct().Count());
        Assert.All(rows, r => Assert.Equal(
            $"{r.GetProperty("kind").GetString()}:{r.GetProperty("item").GetProperty("id").GetGuid()}",
            r.GetProperty("key").GetString()));

        var all = await GetAsync(Reader(server), "/api/v1/inbox");
        Assert.Equal(rows.Count, Array(all, "rows").Count);
    }

    [Fact]
    public async Task The_journal_pages_ascending_from_a_cursor_and_filters_by_kind()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var first = await GetAsync(reader, $"/api/v1/journal?scope={v1.World.RunId}");
        var seqs = Array(first, "events").Select(e => e.GetProperty("seq").GetInt64()).ToList();
        Assert.True(seqs.Count >= 4, $"the world writes several events; got {seqs.Count}");
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs[^1], first.GetProperty("cursor").GetInt64());
        Assert.False(first.GetProperty("hasMore").GetBoolean());

        var page = await GetAsync(reader, $"/api/v1/journal?scope={v1.World.RunId}&since={seqs[0]}&limit=2");
        Assert.Equal(seqs.Skip(1).Take(2), Array(page, "events").Select(e => e.GetProperty("seq").GetInt64()));
        Assert.True(page.GetProperty("hasMore").GetBoolean());

        var verified = await GetAsync(reader, $"/api/v1/journal?scope={v1.World.RunId}&kinds=verification");
        var only = Assert.Single(Array(verified, "events"));
        Assert.Equal("verified", only.GetProperty("kind").GetString());
        Assert.Equal("run-a", only.GetProperty("run").GetProperty("ref").GetString());
        Assert.Equal(JsonValueKind.Object, only.GetProperty("payload").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/v1/journal?kinds=nonsense", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_backlog_groups_open_followups_by_the_run_they_came_from()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var deferred = await v1.World.Writer.StatusAsync(
            v1.World.WorkspaceId, "1.3", "deferred", Write.WriteSeed.Orchestrator,
            new Core.Writing.StatusArgs(Target: "backlog", Reason: "not this run"), Ct);
        Assert.False(deferred.Refused, deferred.Text);

        await using var server = await v1.StartAsync(fixture);
        var backlog = await GetAsync(Reader(server), $"/api/v1/backlog?project={v1.World.ProjectId}");

        Assert.Equal("run", backlog.GetProperty("run").GetProperty("kind").GetString());
        var group = Assert.Single(Array(backlog, "groups"));
        Assert.Equal(v1.World.RunId, group.GetProperty("origin").GetProperty("id").GetGuid());
        Assert.Equal("followup", Assert.Single(Array(group, "items")).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Sessions_list_what_each_holds_and_filter_exactly()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var sessions = await GetAsync(reader, "/api/v1/sessions?active=true");
        var row = Assert.Single(Array(sessions, "sessions"));
        var session = row.GetProperty("session");
        Assert.Equal(v1.SessionId, session.GetProperty("id").GetGuid());
        Assert.Equal(V1World.SessionName, session.GetProperty("name").GetString());
        Assert.Equal("claude-v1", session.GetProperty("claudeSessionId").GetString());
        Assert.Equal("skills-5d", session.GetProperty("launchedBy").GetString());
        Assert.Equal("claude-code 2.1", session.GetProperty("client").GetString());
        Assert.Equal("orchestrator", session.GetProperty("role").GetString());
        Assert.Equal("1.1", Assert.Single(row.GetProperty("claims").EnumerateArray()).GetProperty("item").GetProperty("ref").GetString());

        Assert.Single(Array(await GetAsync(reader, "/api/v1/sessions?claudeSessionId=claude-v1"), "sessions"));
        Assert.Empty(Array(await GetAsync(reader, "/api/v1/sessions?name=aw-v"), "sessions"));
    }

    [Fact]
    public async Task Resolve_turns_a_ref_into_ids()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var resolved = await GetAsync(reader, "/api/v1/resolve?ref=1.1");
        Assert.Equal(v1.World.TaskId, Assert.Single(Array(resolved, "matches")).GetProperty("id").GetGuid());

        Assert.Empty(Array(await GetAsync(reader, "/api/v1/resolve?ref=no-such"), "matches"));
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/v1/resolve", Ct)).StatusCode);
    }
}

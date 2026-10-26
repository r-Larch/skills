using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkState.Api.App.V1;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// doc 13 "Prompt actions" and doc 10's <c>GET /api/v1/prompts</c>: each action is present exactly
/// when it applies, its count is the number the agent it starts will see, every prompt opens with a
/// <c>work_brief</c> on a ref that resolves to the scope alone, and every prompt that closes,
/// cancels, defers or archives says to propose first and wait.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class V1PromptTests(PostgresFixture fixture)
{
    [GeneratedRegex(@"\bwork_[a-z_]+\(")]
    private static partial Regex Call();

    [GeneratedRegex(@"^work_brief\(scope:""([^""]+)""")]
    private static partial Regex BriefCall();

    /// <summary>
    /// run-a: 1.1 open task, 1.2 blocked, 1.3 in_progress with an expired claim, 1.4 open with a note
    /// addressed to it. run-done: closed, its 9.1 done, and a second 1.2 so run-a's is ambiguous.
    /// backlog: two open follow-ups.
    /// </summary>
    private sealed record PromptWorld(WriteWorld World, Guid Blocked, Guid Claimed, Guid Ready, Guid DoneRun, Guid DoneTask, Guid Backlog);

    private async Task<PromptWorld> CreateAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var writer = world.Writer;

        var blocked = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Blocked);
        var claimed = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");
        var ready = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4");

        Ok(await writer.StatusAsync(
            world.WorkspaceId, "1.3", "in_progress", WriteSeed.Orchestrator,
            new StatusArgs(Tree: $"P:/trees/prompts-{world.RunId:n}"), Ct));
        await WriteSeed.ExpireClaimAsync(world, claimed);
        Ok(await writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "decision", "read the catalogue first", "1.4", null, Ct));

        var doneTask = await WriteSeed.SecondRunAsync(world, "run-done");
        var doneRun = (await world.ItemAsync("run-done")).Id;
        await WriteSeed.ChildAsync(world, doneRun, "1.2", status: ItemStatus.Done);
        await WriteSeed.StatusAsync(world, doneTask, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, doneRun, ItemStatus.Done);

        Guid backlog;
        await using (var db = fixture.NewContext())
        {
            var run = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", "backlog", 50m, WorkWriter.BacklogRunTitle);
            db.Items.Add(run);
            await db.SaveChangesAsync(Ct);
            db.Items.AddRange(
                Seed.NewItem(world.WorkspaceId, run.Id, "followup", "B1", 1m, "a later idea"),
                Seed.NewItem(world.WorkspaceId, run.Id, "followup", "B2", 2m, "another later idea"));
            await db.SaveChangesAsync(Ct);
            backlog = run.Id;
        }

        return new PromptWorld(world, blocked, claimed, ready, doneRun, doneTask, backlog);
    }

    private static void Ok(WriteResult result) => Assert.False(result.Refused, result.Text);

    private static async Task<ScopeResult.Found> ScopeAsync(WriteWorld world, Guid id)
    {
        var refs = await new ApiQueries(world.Fixture.DataSource).RefsAsync([id], Ct);
        return new ScopeResult.Found(world.WorkspaceId, Assert.Single(refs).Scope);
    }

    private static async Task<IReadOnlyList<PromptAction>> PromptsAsync(WriteWorld world, Guid id)
        => await world.Reader.PromptsAsync(await ScopeAsync(world, id), Ct);

    private static IReadOnlyList<string> Keys(IEnumerable<PromptAction> prompts) => [.. prompts.Select(p => p.Key)];

    [Fact]
    public async Task A_project_with_open_work_offers_continue_status_housekeeping_and_triage_but_not_finalize()
    {
        var w = await CreateAsync();

        var prompts = await PromptsAsync(w.World, w.World.ProjectId);

        Assert.Equal(["continue", "status", "housekeeping", "triage"], Keys(prompts));
        Assert.Null(prompts[0].Count);
        Assert.Null(prompts[1].Count);
    }

    [Fact]
    public async Task The_housekeeping_count_is_every_work_threads_row_plus_the_expired_claims_attention_names()
    {
        var w = await CreateAsync();
        var scope = await ScopeAsync(w.World, w.World.ProjectId);

        var threads = (await w.World.Reader.ThreadsAsync(scope, [], Ct)).Split('\n');
        // A row starts at column 0 with its ref; a wrapped text or tail line is indented under it.
        var rows = threads.Skip(2).Count(l => l.Length > 0 && l[0] != ' ');
        var expired = (await w.World.Reader.AttentionAsync(scope, Ct)).Count(l => l.Rank == AttentionRank.ExpiredClaim);

        Assert.Contains(threads, l => l.Contains("note", StringComparison.Ordinal) && l.StartsWith("1.4", StringComparison.Ordinal));
        Assert.Equal(1, expired);

        var housekeeping = Assert.Single(await PromptsAsync(w.World, w.World.ProjectId), p => p.Key == "housekeeping");
        Assert.Equal(rows + expired, housekeeping.Count);
        Assert.Contains($"{rows + expired} unclosed thread(s)", housekeeping.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_triage_count_is_the_backlog_the_read_api_and_work_threads_show()
    {
        var w = await CreateAsync();
        await using var server = await V1Http.StartAsync(fixture, w.World.Slug);
        var reader = V1Http.Reader(server);

        var backlog = await V1Http.GetAsync(reader, $"/api/v1/backlog?project={w.World.ProjectId}");
        var listed = V1Http.Array(backlog, "groups").Sum(g => g.GetProperty("items").GetArrayLength());
        var followups = (await w.World.Reader.ThreadsAsync(await ScopeAsync(w.World, w.World.ProjectId), ["followups"], Ct))
            .Split('\n').Count(l => l.Contains("  foll  ", StringComparison.Ordinal));

        var triage = Assert.Single(await PromptsAsync(w.World, w.World.ProjectId), p => p.Key == "triage");
        Assert.Equal(2, listed);
        Assert.Equal(listed, triage.Count);
        Assert.Equal(followups, triage.Count);
    }

    [Fact]
    public async Task Finalize_applies_when_only_the_backlog_has_open_work_and_never_to_an_archived_project()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.DoesNotContain("finalize", Keys(await PromptsAsync(world, world.ProjectId)));

        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await using (var db = fixture.NewContext())
        {
            var backlog = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", "backlog", 50m, WorkWriter.BacklogRunTitle);
            db.Items.Add(backlog);
            await db.SaveChangesAsync(Ct);
            db.Items.Add(Seed.NewItem(world.WorkspaceId, backlog.Id, "followup", "B1", 1m, "later"));
            await db.SaveChangesAsync(Ct);
        }

        var prompts = await PromptsAsync(world, world.ProjectId);
        Assert.Equal(["continue", "status", "housekeeping", "triage", "finalize"], Keys(prompts));
        var projectRef = (await ScopeAsync(world, world.ProjectId)).Item.Ref;
        Assert.Contains(
            $"work_update(ref:\"{projectRef}\", archived:true)",
            Assert.Single(prompts, p => p.Key == "finalize").Text,
            StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            (await db.Items.FindAsync([world.ProjectId], Ct))!.ArchivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        Assert.DoesNotContain("finalize", Keys(await PromptsAsync(world, world.ProjectId)));
    }

    [Fact]
    public async Task A_run_offers_resume_while_open_and_audit_once_it_has_done_work()
    {
        var w = await CreateAsync();

        Assert.Equal(["resume"], Keys(await PromptsAsync(w.World, w.World.RunId)));
        Assert.Equal(["audit"], Keys(await PromptsAsync(w.World, w.DoneRun)));

        await WriteSeed.StatusAsync(w.World, w.World.TaskId, ItemStatus.Done);
        Assert.Equal(["resume", "audit"], Keys(await PromptsAsync(w.World, w.World.RunId)));
    }

    [Fact]
    public async Task An_item_offers_work_when_open_or_ready_and_unblock_when_blocked()
    {
        var w = await CreateAsync();

        Assert.Equal(["work"], Keys(await PromptsAsync(w.World, w.Ready)));
        Assert.Equal(["work"], Keys(await PromptsAsync(w.World, w.World.TaskId)));
        Assert.Equal(["unblock"], Keys(await PromptsAsync(w.World, w.Blocked)));
        Assert.Empty(await PromptsAsync(w.World, w.Claimed));
        Assert.Empty(await PromptsAsync(w.World, w.DoneTask));
        Assert.Empty(await PromptsAsync(w.World, w.World.PhaseId));

        await WriteSeed.StatusAsync(w.World, w.Ready, ItemStatus.Ready);
        Assert.Equal(["work"], Keys(await PromptsAsync(w.World, w.Ready)));
    }

    [Fact]
    public async Task Every_prompt_opens_with_work_brief_on_a_ref_that_resolves_to_its_scope_alone()
    {
        var w = await CreateAsync();
        var board = new BoardQueries(fixture.DataSource);
        var seen = 0;

        foreach (var id in (Guid[])[w.World.ProjectId, w.World.RunId, w.DoneRun, w.World.TaskId, w.Blocked, w.Ready])
        {
            foreach (var prompt in await PromptsAsync(w.World, id))
            {
                var first = Call().Match(prompt.Text);
                Assert.True(first.Success, $"{prompt.Key}: no call at all");
                var brief = BriefCall().Match(prompt.Text[first.Index..]);
                Assert.True(brief.Success, $"{prompt.Key}: the first call is not work_brief(scope:…):\n{prompt.Text}");

                var matches = await board.ResolveAsync(w.World.WorkspaceId, brief.Groups[1].Value, Ct);
                Assert.True(matches is [var only] && only.Id == id, $"{prompt.Key}: {brief.Groups[1].Value} does not resolve to its scope alone");
                seen++;
            }
        }

        Assert.True(seen >= 9, $"only {seen} prompts were checked");
    }

    [Fact]
    public async Task An_ambiguous_plain_ref_is_printed_qualified()
    {
        var w = await CreateAsync();

        var unblock = Assert.Single(await PromptsAsync(w.World, w.Blocked));

        Assert.Contains("work_brief(scope:\"run-a/1.2\")", unblock.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("scope:\"1.2\"", unblock.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule is about what the text tells an agent to do, so it is judged on the text: any prompt
    /// naming a close, cancel, deferral or archive carries the sentence, whatever its entry declares.
    /// </summary>
    [Fact]
    public async Task Every_prompt_that_closes_cancels_defers_or_archives_says_propose_first_and_wait()
    {
        var w = await CreateAsync();
        var finalizable = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(finalizable, finalizable.TaskId, ItemStatus.Done);

        var prompts = new List<PromptAction>();
        foreach (var id in (Guid[])[w.World.ProjectId, w.World.RunId, w.DoneRun, w.World.TaskId, w.Blocked])
        {
            prompts.AddRange(await PromptsAsync(w.World, id));
        }

        prompts.AddRange(await PromptsAsync(finalizable, finalizable.ProjectId));

        string[] writes = ["to:\"done\"", "to:\"cancelled\"", "to:\"deferred\"", "archived:true"];
        var writing = prompts.Where(p => writes.Any(x => p.Text.Contains(x, StringComparison.Ordinal))).ToList();

        Assert.Equal(["finalize", "housekeeping"], writing.Select(p => p.Key).Distinct().Order(StringComparer.Ordinal));
        Assert.All(writing, p => Assert.Contains(PromptCatalogue.ProposeAndWait, p.Text, StringComparison.Ordinal));
        Assert.Contains(PromptCatalogue.ProposeAndWait, Assert.Single(prompts, p => p.Key == "triage").Text, StringComparison.Ordinal);
        Assert.All(
            PromptCatalogue.Entries.Where(e => e.Writes),
            e => Assert.Contains(PromptCatalogue.ProposeAndWait, e.Text(new PromptFacts(e.Scope, "x", "task", "t", ItemStatus.Open)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_endpoint_returns_the_applicable_prompts_whole_for_a_reader_and_writes_nothing()
    {
        var w = await CreateAsync();
        await using var server = await V1Http.StartAsync(fixture, w.World.Slug);
        var reader = V1Http.Reader(server);

        var json = await V1Http.GetAsync(reader, $"/api/v1/prompts?scope={w.World.ProjectId}");
        var direct = await PromptsAsync(w.World, w.World.ProjectId);

        Assert.Equal(w.World.ProjectId, json.GetProperty("scope").GetProperty("id").GetGuid());
        var listed = V1Http.Array(json, "prompts");
        Assert.Equal(Keys(direct), [.. listed.Select(p => p.GetProperty("key").GetString()!)]);
        Assert.Equal(direct.Select(p => p.Text), listed.Select(p => p.GetProperty("text").GetString()));
        Assert.Equal(direct.Select(p => p.Label), listed.Select(p => p.GetProperty("label").GetString()));
        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("count").ValueKind);
        Assert.Equal(2, listed.Single(p => p.GetProperty("key").GetString() == "triage").GetProperty("count").GetInt32());

        // Read-only: the journal does not move, the lapsed claim is not swept, the note is not consumed.
        var seq = json.GetProperty("seq").GetInt64();
        foreach (var id in (Guid[])[w.World.RunId, w.Ready, w.Claimed, w.Blocked])
        {
            await V1Http.GetAsync(reader, $"/api/v1/prompts?scope={id}");
        }

        var after = await V1Http.GetAsync(reader, $"/api/v1/prompts?scope={w.World.ProjectId}");
        Assert.Equal(seq, after.GetProperty("seq").GetInt64());
        Assert.Equal(
            V1Http.Array(json, "prompts").Select(p => p.GetRawText()),
            V1Http.Array(after, "prompts").Select(p => p.GetRawText()));

        var cookie = await server.SignInAsync(Ct);
        await V1Http.GetAsync(cookie, $"/api/v1/prompts?scope={w.Ready}");
    }

    [Fact]
    public async Task The_endpoint_refuses_no_credential_an_unknown_id_a_missing_scope_and_a_write()
    {
        var w = await CreateAsync();
        await using var server = await V1Http.StartAsync(fixture, w.World.Slug);

        var anonymous = await server.NewClient().GetAsync($"/api/v1/prompts?scope={w.World.ProjectId}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(ApiV1.Version, Assert.Single(anonymous.Headers.GetValues(ApiV1.Header)));

        var reader = V1Http.Reader(server);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/prompts?scope={Guid.CreateVersion7()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/v1/prompts", Ct)).StatusCode);

        var post = await reader.PostAsync($"/api/v1/prompts?scope={w.World.ProjectId}", null, Ct);
        Assert.True(
            post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"POST /api/v1/prompts → {(int)post.StatusCode}");
    }
}

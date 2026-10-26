using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// noclip, the behavioural half of <see cref="NoClipSourceTests"/>. Every text field holds a long
/// sentinel no other field shares; every brief, detail view, history, refusal and resource is
/// rendered; and wherever a view shows a sentinel at all, it shows it whole. Rows of a list are the
/// one exception docs/workstate/06-rendering.md allows, and they are named here as such.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class NoClipBehaviourTests(PostgresFixture fixture)
{
    private static readonly string Title = Sentinel("ttlq");
    private static readonly string Goal = Sentinel("golq");
    private static readonly string Intent = Sentinel("intq");
    private static readonly string Criterion = Sentinel("crtq");
    private static readonly string Evidence = Sentinel("evdq");
    private static readonly string Verify = "dotnet test --filter " + Sentinel("vfyq");
    private static readonly string Addressed = Sentinel("ntaq");
    private static readonly string Carried = Sentinel("ntcq");
    private static readonly string Rule = Sentinel("cnsq");
    private static readonly string Route = Sentinel("rteq");
    private static readonly string Assumption = Sentinel("asmq");
    private static readonly string Finding = Sentinel("fndq");
    private static readonly string Reason = Sentinel("rsnq");
    private static readonly string Report = Sentinel("rptq");

    private static readonly string[] All =
        [Title, Goal, Intent, Criterion, Evidence, Verify, Addressed, Carried, Rule, Route, Assumption, Finding, Reason, Report];

    [Fact]
    public async Task Every_view_that_shows_a_text_shows_it_whole_and_no_call_carries_a_cut()
    {
        var world = await SentinelWorldAsync();
        var reader = world.Reader;

        var run = await world.ScopeAsync("run-a");
        var task = await world.ScopeAsync("1.1");

        // Rendered in-process first: none of these consume a note or move the task.
        var views = new Dictionary<string, (string Text, string[] Shows)>
        {
            ["work_brief run"] = (await reader.BriefAsync(run, "resume", Ct), [Goal, Rule, Route, Assumption, Addressed, Carried]),
            ["work_brief task"] = (await reader.BriefAsync(task, "resume", Ct), [Intent, Rule, Route]),
            ["work_brief worker"] = (await reader.BriefAsync(task, "worker", Ct),
                [Title, Intent, Criterion, Verify, Addressed, Rule, Route]),
            ["work_get"] = (await reader.GetAsync(run, ["1.1", "F-01"], ["deps"], Ct),
                [Title, Intent, Criterion, Evidence, Report, Finding]),
            ["work_next"] = (await reader.NextAsync(run, 10, null, Ct), [Verify]),
            ["work_threads"] = (await reader.ThreadsAsync(run, [], Ct), [Assumption, Addressed]),
            ["work_history"] = (await reader.HistoryAsync(world.WorkspaceId, null, 0, 100, Ct),
                [Addressed, Carried, Reason, Finding, Rule]),
            ["conventions"] = (await reader.ConventionsAsync(run, Ct), [Rule, Route]),
        };

        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        var guard = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);
        Assert.True(guard.Refused, guard.Text);
        views["done guard"] = (guard.Text, [Criterion, Verify]);

        await using (var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct))
        await using (var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct))
        {
            views["work://item/1.1"] = (await ReadAsync(client, WorkUris.Item("1.1")),
                [Title, Intent, Criterion, Evidence, Report]);
            views["work://item/1.1/brief"] = (await ReadAsync(client, WorkUris.ItemBrief("1.1")),
                [Title, Intent, Criterion, Verify, Addressed, Rule, Route]);
        }

        // List rows doc 06 allows to shorten: each carries the ref whose detail view prints it whole.
        var rows = new Dictionary<string, string[]>
        {
            ["work_threads"] = [Finding],
            ["work_next"] = [Title, Finding],
        };

        foreach (var (name, (text, shows)) in views)
        {
            var body = WithoutAttention(text);
            var folded = Fold(body);

            foreach (var sentinel in shows)
            {
                Assert.True(
                    folded.Contains(sentinel, StringComparison.Ordinal),
                    $"{name} does not show {Tag(sentinel)} whole:\n{text}");
            }

            foreach (var sentinel in All.Except(rows.GetValueOrDefault(name, [])))
            {
                if (folded.Contains(Opening(sentinel), StringComparison.Ordinal))
                {
                    Assert.True(
                        folded.Contains(sentinel, StringComparison.Ordinal),
                        $"{name} shows {Tag(sentinel)} but not whole:\n{text}");
                }
            }

            foreach (var call in Calls().Matches(text).Select(m => m.Value))
            {
                Assert.False(Cut().IsMatch(call), $"{name} prints a call with a cut in it: {call}");
            }
        }
    }

    private async Task<WriteWorld> SentinelWorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await using (var db = fixture.NewContext())
        {
            var task = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);
            task.Title = Title;
            task.Intent = Intent;
            task.VerifyCmd = Verify;

            var run = await db.Items.FirstAsync(i => i.Id == world.RunId, Ct);
            run.Intent = Goal;

            db.Verifications.Add(new Verification
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                ItemId = world.TaskId,
                Command = "dotnet build",
                ResultLine = Evidence,
                Passed = true,
                RunBy = VerifyRunner.Worker,
            });

            await db.SaveChangesAsync(Ct);
        }

        await WriteSeed.CriterionAsync(world, world.TaskId, 1, Criterion, ticked: false);

        var writer = world.Writer;
        var actor = WriteSeed.Orchestrator;

        Ok(await writer.NoteAsync(world.WorkspaceId, actor, "carry_forward", Addressed, "1.1", null, Ct));
        Ok(await writer.NoteAsync(world.WorkspaceId, actor, "decision", Carried, null, "1.1", Ct));
        Ok(await writer.ConstrainAsync(
            world.WorkspaceId, actor, null, new ConstrainArgs(Kind: "red_line", Text: Rule, Route: Route), Ct));
        Ok(await writer.AssumeAsync(
            world.WorkspaceId, actor, "run-a", Assumption, "reasoned", "medium", null, null, null, Ct));
        Ok(await writer.FindingAsync(
            world.WorkspaceId, actor, [new NewFinding("2", Finding, "minor")], Ct));
        Ok(await writer.ReportAsync(
            world.WorkspaceId, "1.1", WriteSeed.Worker, new ReportArgs("partial", NotDone: Report), Ct));

        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        Ok(await writer.StatusAsync(
            world.WorkspaceId, "1.2", "cancelled", actor, new StatusArgs(Reason: Reason), Ct));

        return world;
    }

    private static void Ok(WriteResult result) => Assert.False(result.Refused, result.Text);

    private static async Task<string> ReadAsync(McpClient client, string uri)
    {
        var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
        return string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }

    /// <summary>
    /// A text of at least 300 characters made of words no other sentinel shares: <c>tag0 tag1 …</c>.
    /// A cut copy still opens with <c>tag0 tag1</c>, which is how a view is caught showing part of it.
    /// </summary>
    private static string Sentinel(string tag)
    {
        var words = new List<string>();
        for (var i = 0; string.Join(" ", words).Length < 300; i++)
        {
            words.Add($"{tag}{i}");
        }

        return string.Join(" ", words);
    }

    private static string Tag(string sentinel) => TagOf().Match(sentinel).Groups[1].Value;

    private static string Opening(string sentinel) => $"{Tag(sentinel)}0 {Tag(sentinel)}1 ";

    [GeneratedRegex(@"\b([a-z]{3}q)0 ")]
    private static partial Regex TagOf();

    private static string Fold(string text) => Spaces().Replace(text, " ");

    /// <summary>The attention block is a list of one-line rows (doc 06 §5), each carrying its ref.</summary>
    private static string WithoutAttention(string text)
    {
        var kept = new List<string>();
        var inBlock = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("--- attention", StringComparison.Ordinal))
            {
                inBlock = true;
                continue;
            }

            inBlock = inBlock && line.StartsWith(Glyph.Warn, StringComparison.Ordinal);
            if (!inBlock)
            {
                kept.Add(line);
            }
        }

        return string.Join("\n", kept);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A tool call, its arguments up to the matching parenthesis (one level of nesting).</summary>
    [GeneratedRegex(@"\bwork_\w+\((?:[^()]|\([^()]*\))*\)")]
    private static partial Regex Calls();

    /// <summary>
    /// An ellipsis glued to a word is a cut; <c>"…"</c>, <c>[…]</c> and <c>, …)</c> are placeholders
    /// the caller fills in.
    /// </summary>
    [GeneratedRegex(@"[\p{L}\p{N}]…")]
    private static partial Regex Cut();
}

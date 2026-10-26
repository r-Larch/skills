using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// ws/2. A controller pastes the worker brief into a worker's prompt, and it arrived with every red
/// line cut at ~95 columns — the route, the "what to do instead", was the part that went missing. A
/// rule and a task's intent are binding content, so they wrap and never clip.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class FullBriefTests(PostgresFixture fixture)
{
    private const string RuleText =
        "Never stop, rm, respawn, attach to, message, or read the key file of any Claude session "
        + "this controller did not start itself, and never treat a session that merely looks idle "
        + "as abandoned, because a human may be attached to it and reading";

    private const string RouteText =
        "ListAgents shows who started what; a session you did not start is reported to the "
        + "controller with its name and left exactly as it is";

    private const string IntentText =
        "Render the worker brief so that every binding rule, its route and the task intent reach "
        + "the worker whole: wrapped at the hundred-column budget with an indented continuation, "
        + "never cut with an ellipsis, because the worker acts on the end of a sentence as much as "
        + "on its start, and the end is where the route lives";

    [Fact]
    public async Task The_worker_brief_and_its_resource_carry_a_long_rule_its_route_and_the_intent_in_full()
    {
        var world = await LongWorldAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var tool = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1", ["for"] = "worker" },
            Ct);

        var read = await client.ReadResourceAsync(WorkUris.ItemBrief("1.1"), cancellationToken: Ct);
        var resource = string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        foreach (var brief in new[] { tool, resource })
        {
            AssertWhole(brief, $"{RuleText} — {RouteText}");

            // The task has no run goal above it, so GOAL is the intent as well: both must be whole.
            AssertWhole(brief, IntentText);
            AssertWhole(brief[brief.IndexOf("YOUR TASK", StringComparison.Ordinal)..], IntentText);
        }
    }

    /// <summary>
    /// The resume brief caps BINDING at five and points at the conventions for the rest; a worker
    /// brief pasted into a prompt has no such pointer to follow, so rule six would simply be gone.
    /// </summary>
    [Fact]
    public async Task The_worker_brief_and_its_resource_carry_every_rule_in_scope_past_the_resume_cap()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        for (var i = 1; i <= 7; i++)
        {
            var wrote = await world.Writer.ConstrainAsync(
                world.WorkspaceId,
                WriteSeed.Orchestrator,
                null,
                new ConstrainArgs(Kind: "command", Text: $"binding rule number {i} of seven"),
                Ct);

            Assert.False(wrote.Refused, wrote.Text);
        }

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var tool = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1", ["for"] = "worker" },
            Ct);

        var read = await client.ReadResourceAsync(WorkUris.ItemBrief("1.1"), cancellationToken: Ct);
        var resource = string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        foreach (var brief in new[] { tool, resource })
        {
            Assert.Contains("BINDING (7)", brief, StringComparison.Ordinal);
            Assert.DoesNotContain(WorkUris.Conventions, brief, StringComparison.Ordinal);

            for (var i = 1; i <= 7; i++)
            {
                Assert.Contains(
                    $"  · {Glyph.Pad($"C{i}", 4)} binding rule number {i} of seven",
                    brief,
                    StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// ws/3. A worker does not own its task's status — the orchestrator claims, verifies and closes,
    /// and a worker token cannot call work_status at all. The worker brief told it to anyway.
    /// </summary>
    [Fact]
    public async Task The_worker_brief_on_a_claimed_task_names_work_report_and_never_work_status()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var claimed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "in_progress", WriteSeed.Orchestrator, new StatusArgs(), Ct);
        Assert.False(claimed.Refused, claimed.Text);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var tool = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1", ["for"] = "worker" },
            Ct);

        var read = await client.ReadResourceAsync(WorkUris.ItemBrief("1.1"), cancellationToken: Ct);
        var resource = string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        foreach (var brief in new[] { tool, resource })
        {
            Assert.DoesNotContain("work_status(", brief, StringComparison.Ordinal);
            Assert.Matches(@"(?m)^NEXT   .*work_report\(ref:""1\.1""", brief);
        }
    }

    /// <summary>The resume brief belongs to the orchestrator, and its NEXT is the closing pair.</summary>
    [Fact]
    public async Task The_resume_brief_on_the_same_claimed_task_keeps_its_work_status_NEXT()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var claimed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "in_progress", WriteSeed.Orchestrator, new StatusArgs(), Ct);
        Assert.False(claimed.Refused, claimed.Text);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var brief = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1" },
            Ct);

        Assert.Contains(
            "NEXT   work_verify(ref:\"1.1\", result_line:\"…\", run_by:\"orchestrator\") "
            + "then work_status(ref:\"1.1\", to:\"done\")",
            brief.Split('\n'));
    }

    /// <summary>
    /// ws/3. A done-criterion is what the worker is judged against and a note addressed to the task
    /// is what the last session needed it to know; the worker brief cut both at the column budget.
    /// </summary>
    [Fact]
    public async Task The_worker_brief_carries_a_long_criterion_and_a_long_note_in_full()
    {
        const string criterion =
            "numbers recorded in docs/LEARNINGS.md: build time, binary size, idle RSS, p95 first-byte "
            + "latency under the battle-test load, and the cold-start time of the container measured "
            + "from docker run to the first healthy probe";

        const string note =
            "The previous attempt measured idle RSS with the dashboard tab open, which pins the SSE "
            + "stream and doubles the number; close every client first, wait a full minute, then read "
            + "the value from docker stats rather than from /proc";

        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 3, criterion, ticked: false);

        var noted = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", note, "1.1", null, Ct);
        Assert.False(noted.Refused, noted.Text);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var brief = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1", ["for"] = "worker" },
            Ct);

        AssertWhole(brief, criterion);
        AssertWhole(brief, note);
        Assert.All(brief.Split('\n'), l => Assert.True(l.Length <= Columns.Width, $"{l.Length} columns: {l}"));
    }

    /// <summary>
    /// ws/4. The worker brief showed three addressed notes and consumed all of them, so the fourth
    /// and fifth were marked delivered to an agent that never saw them.
    /// </summary>
    [Fact]
    public async Task The_worker_brief_renders_every_note_addressed_to_the_task_and_consumes_exactly_those()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        for (var i = 1; i <= 5; i++)
        {
            var noted = await world.Writer.NoteAsync(
                world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", $"addressed note number {i} of five", "1.1", null, Ct);
            Assert.False(noted.Refused, noted.Text);
        }

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var brief = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1", ["for"] = "worker" },
            Ct);

        Assert.Contains("ADDRESSED TO YOU (5)", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("more unconsumed in scope", brief, StringComparison.Ordinal);

        for (var i = 1; i <= 5; i++)
        {
            Assert.Contains($"  addressed note number {i} of five", brief.Split('\n'));
        }

        await using var db = fixture.NewContext();
        var unconsumed = await db.Notes.CountAsync(n => n.ToItemId == world.TaskId && n.ConsumedAt == null, Ct);
        Assert.Equal(0, unconsumed);
    }

    /// <summary>
    /// ws/4. Consumption is by the ids the brief rendered, not by "everything addressed to the item
    /// right now": a note written after the render was never shown, so it must survive the update.
    /// </summary>
    [Fact]
    public async Task A_note_written_after_the_brief_rendered_stays_unconsumed()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var first = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "the note the brief rendered", "1.1", null, Ct);
        Assert.False(first.Refused, first.Text);

        Guid rendered;
        await using (var db = fixture.NewContext())
        {
            rendered = (await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).Id;
        }

        var late = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "a note written after the render", "1.1", null, Ct);
        Assert.False(late.Refused, late.Text);

        var consumed = await WriteSeed.Keeper(fixture).ConsumeNotesAsync(world.TaskId, [rendered], Ct);

        Assert.Equal(1, consumed);

        await using var after = fixture.NewContext();
        var left = await after.Notes
            .Where(n => n.ToItemId == world.TaskId && n.ConsumedAt == null)
            .Select(n => n.Body)
            .ToListAsync(Ct);

        Assert.Equal(["a note written after the render"], left);
    }

    /// <summary>
    /// The text is wrapped, so it is found with its line breaks folded back into spaces; the lines
    /// that carry it must still fit the width budget and hold no ellipsis.
    /// </summary>
    private static void AssertWhole(string brief, string text)
    {
        var lines = brief.Split('\n');
        var folded = Regex.Replace(brief, @"\s+", " ");

        Assert.Contains(text, folded, StringComparison.Ordinal);

        var first = Array.FindIndex(lines, l => l.Contains(text[..40], StringComparison.Ordinal));
        Assert.True(first >= 0, $"no line opens the text:\n{brief}");

        var last = first;
        while (last < lines.Length
               && !Regex.Replace(string.Join(" ", lines[first..(last + 1)]), @"\s+", " ")
                   .Contains(text, StringComparison.Ordinal))
        {
            last++;
        }

        Assert.True(last < lines.Length, $"the text is not carried by consecutive lines:\n{brief}");

        var span = lines[first..(last + 1)];
        Assert.True(span.Length > 1, $"a {text.Length}-char text did not wrap:\n{string.Join("\n", span)}");
        Assert.All(span, l => Assert.DoesNotContain(Glyph.Ellipsis, l, StringComparison.Ordinal));
        Assert.All(span, l => Assert.True(l.Length <= Columns.Width, $"{l.Length} columns: {l}"));
    }

    private async Task<WriteWorld> LongWorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var wrote = await world.Writer.ConstrainAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            null,
            new ConstrainArgs(Kind: "red_line", Text: RuleText, Route: RouteText),
            Ct);

        Assert.False(wrote.Refused, wrote.Text);

        await using var db = fixture.NewContext();
        var task = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);
        task.Intent = IntentText;
        await db.SaveChangesAsync(Ct);

        return world;
    }
}

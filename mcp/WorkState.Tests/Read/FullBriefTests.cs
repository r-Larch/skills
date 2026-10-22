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

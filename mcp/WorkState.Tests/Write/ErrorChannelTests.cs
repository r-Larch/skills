using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 04 §3 as a gate rather than an intention: <b>no error may state a problem without naming the
/// call that fixes it.</b> One regex — <see cref="RefusalShape"/>'s — over every refusal path the
/// server can be driven into over the real transport, read and write alike.
/// <para>
/// The catalogue is deliberately a list rather than a sample. An error channel tested on the one
/// refusal someone happened to think of is the *"a check that could not have come out the other way"*
/// defect this project's own corpus names as the highest-yield thing to look for.
/// </para>
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ErrorChannelTests(PostgresFixture fixture)
{
    /// <summary>
    /// Every refusal an agent can provoke from a populated workspace, each named by the mistake that
    /// produces it. A new guard that does not appear here is a guard nothing holds to doc 04 §3.
    /// </summary>
    [Fact]
    public async Task Every_refusal_the_server_can_emit_names_a_callable_fix()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);

        // "1.1" now exists in both runs, which the schema allows and doc 01 §4 calls ambiguous.
        await WriteSeed.ChildAsync(world, await RunBAsync(world), "1.1");

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var covered = new List<string>();

        foreach (var (name, tool, arguments) in Catalogue())
        {
            var (text, _) = await ReadServer.CallEitherAsync(client, tool, arguments, Ct);

            Assert.True(
                text.StartsWith("REFUSED", StringComparison.Ordinal),
                $"{name}: expected a refusal from {tool}, got:\n{text}");

            RefusalShape.SelfCorrecting(text);
            covered.Add(name);
        }

        Assert.Equal(Catalogue().Count, covered.Count);
    }

    /// <summary>
    /// doc 06 §6's third rule, and doc 04's acceptance check 2 read from the other side: the override
    /// is named — hiding it is how an escape hatch gets worked around — but it must never be the
    /// most readable thing in the refusal. The remedies come first and there are more of them.
    /// </summary>
    [Fact]
    public async Task A_refused_close_explains_the_remedy_before_and_more_than_the_override()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "the observable thing", ticked: false);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var (text, refused) = await ReadServer.CallEitherAsync(
            client,
            "work_status",
            new Dictionary<string, object?> { ["ref"] = "1.1", ["to"] = "done" },
            Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        RefusalShape.NamesTheOverride(text);

        var remedies = text.Split('\n').Count(l => l.TrimStart().StartsWith('→'));
        var forceAt = text.IndexOf("force:true", StringComparison.Ordinal);
        var firstRemedyAt = text.IndexOf('→');

        Assert.True(firstRemedyAt < forceAt, "the override is stated before the remedy");
        Assert.True(remedies > 1, $"the refusal offers {remedies} remedy against one override");

        // The honest path is what the agent reaches for, so it is what the refusal spells out.
        Assert.Contains("work_verify(", text, StringComparison.Ordinal);
        Assert.Contains("work_update(", text, StringComparison.Ordinal);
    }

    private async Task<Guid> RunBAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.FirstAsync(
            i => i.WorkspaceId == world.WorkspaceId && i.Ref == "run-b", Ct)).Id;
    }

    private static List<(string Name, string Tool, Dictionary<string, object?> Arguments)> Catalogue() =>
    [
        ("brief on a ref that does not exist", "work_brief",
            new Dictionary<string, object?> { ["scope"] = "no-such-ref" }),

        ("brief on a ref that exists in two runs", "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1" }),

        ("next under a ref that does not exist", "work_next",
            new Dictionary<string, object?> { ["scope"] = "nope-9" }),

        ("tree under a ref that does not exist", "work_tree",
            new Dictionary<string, object?> { ["scope"] = "nope-9" }),

        ("threads under a ref that does not exist", "work_threads",
            new Dictionary<string, object?> { ["scope"] = "nope-9" }),

        ("audit under a ref that does not exist", "work_audit",
            new Dictionary<string, object?> { ["scope"] = "nope-9" }),

        ("history under a ref that does not exist", "work_history",
            new Dictionary<string, object?> { ["scope"] = "nope-9" }),

        ("status to an illegal target", "work_status",
            new Dictionary<string, object?> { ["ref"] = "9.1", ["to"] = "done" }),

        ("status to a state that is not a state", "work_status",
            new Dictionary<string, object?> { ["ref"] = "9.1", ["to"] = "finished" }),

        ("blocked with no reason", "work_status",
            new Dictionary<string, object?> { ["ref"] = "9.1", ["to"] = "blocked" }),

        ("deferred with no target", "work_status",
            new Dictionary<string, object?> { ["ref"] = "9.1", ["to"] = "deferred", ["reason"] = "later" }),

        ("status on a ref that does not exist", "work_status",
            new Dictionary<string, object?> { ["ref"] = "nope-9", ["to"] = "ready" }),

        ("a task with no verification command", "work_create",
            new Dictionary<string, object?>
            {
                ["items"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "task",
                        ["title"] = "Add a /health endpoint",
                    },
                },
            }),

        ("an item of a kind that does not exist", "work_create",
            new Dictionary<string, object?>
            {
                ["items"] = new[]
                {
                    new Dictionary<string, object?> { ["kind"] = "epic", ["title"] = "Something" },
                },
            }),

        ("update of a ref that does not exist", "work_update",
            new Dictionary<string, object?> { ["ref"] = "nope-9", ["title"] = "x" }),

        ("verify with no result line", "work_verify",
            new Dictionary<string, object?> { ["ref"] = "9.1", ["result_line"] = "  " }),

        ("verify on a ref that does not exist", "work_verify",
            new Dictionary<string, object?> { ["ref"] = "nope-9", ["result_line"] = "Passed!" }),

        ("move under a parent that does not exist", "work_move",
            new Dictionary<string, object?> { ["ref"] = "9.1", ["parent"] = "nope-9" }),

        ("link of a type that does not exist", "work_link",
            new Dictionary<string, object?> { ["from"] = "9.1", ["to"] = "2", ["type"] = "relates_to" }),

        ("link from a ref that does not exist", "work_link",
            new Dictionary<string, object?> { ["from"] = "nope-9", ["to"] = "2", ["type"] = "blocks" }),

        ("a note of a kind that does not exist", "work_note",
            new Dictionary<string, object?> { ["kind"] = "thought", ["body"] = "something" }),

        ("a note with no body", "work_note",
            new Dictionary<string, object?> { ["kind"] = "decision", ["body"] = "  " }),

        ("an assumption with no statement", "work_assume",
            new Dictionary<string, object?> { ["ref"] = "9.1" }),

        ("a report on a ref that does not exist", "work_report",
            new Dictionary<string, object?> { ["ref"] = "nope-9", ["status"] = "done" }),

        ("a finding against a target that does not exist", "work_finding",
            new Dictionary<string, object?>
            {
                ["findings"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target"] = "nope-9",
                        ["title"] = "Something is wrong",
                        ["severity"] = "blocker",
                    },
                },
            }),
    ];
}

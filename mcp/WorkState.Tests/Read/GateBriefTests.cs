using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// backlog/brief-variants. <c>work_brief(for:)</c> rendered resume for every value but worker, so the
/// attention line that sent an agent to gate a phase landed on a resume brief. Gate is built; every
/// other value is refused by name.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class GateBriefTests(PostgresFixture fixture)
{
    /// <summary>
    /// Phase 1 holds 1.1 and 1.2; phase 2 holds 2.1 and 2.2. 1.1 blocks 1.2 inside the phase, 1.2
    /// blocks 2.1 across its boundary, and 2.1 blocks 2.2 entirely outside it.
    /// </summary>
    private async Task<WriteWorld> SeamWorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var second = (await world.ItemAsync("2")).Id;
        var inner = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        var crossing = await WriteSeed.ChildAsync(world, second, "2.1");
        var outside = await WriteSeed.ChildAsync(world, second, "2.2");

        await WriteSeed.LinkAsync(world, world.TaskId, inner, LinkType.Blocks);
        await WriteSeed.LinkAsync(world, inner, crossing, LinkType.Blocks);
        await WriteSeed.LinkAsync(world, crossing, outside, LinkType.Blocks);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        return world;
    }

    [Fact]
    public async Task A_gate_brief_on_a_phase_lists_its_tasks_and_every_seam_and_writes_nothing()
    {
        var world = await SeamWorldAsync();
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "for the task", "1.1", null, Ct)).Refused);
        var events = await WriteSeed.EventKindsAsync(world);

        var text = await world.Reader.BriefSessionAsync(
            await world.ScopeAsync("1"), "gate", WriteSeed.Keeper(fixture), Ct, signed: true);

        Assert.StartsWith("GATE 1 — Foundations  ·  run run-a  ·  0 of 2 tasks done\n", text, StringComparison.Ordinal);
        Assert.Contains("\nTASKS (2)\n", text, StringComparison.Ordinal);
        Assert.Matches("\n  1\\.1 +task +open .*\n      verify   ✓ Passed! Failed: 0, Passed: 91 by orchestrator,", text);
        Assert.Matches("\n  1\\.2 +task .*\n      verify   none recorded\n", text);
        Assert.DoesNotMatch("\n  2\\.[12] ", text);

        Assert.Contains("\nSEAMS (2) — work_link edges between these tasks or across the phase boundary\n", text, StringComparison.Ordinal);
        Assert.Contains("\n  inner     1.1 blocks → 1.2\n", text, StringComparison.Ordinal);
        Assert.Contains("\n  crossing  1.2 blocks → 2.1\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2.1 blocks → 2.2", text, StringComparison.Ordinal);

        Assert.Contains("work_verify(ref:\"1\", result_line:\"…\", run_by:\"orchestrator\")  then  work_status(ref:\"1\", to:\"done\")", text, StringComparison.Ordinal);
        Assert.Contains("work_finding(findings:[{target:\"1\", title:\"…\", severity:\"blocker\", source:\"gate\"}])", text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Null((await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).ConsumedAt);
        Assert.Equal(events, await WriteSeed.EventKindsAsync(world));
    }

    [Fact]
    public async Task A_gate_brief_on_anything_but_a_phase_is_refused_with_the_route_to_its_phase()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var events = await WriteSeed.EventKindsAsync(world);

        foreach (var scope in new[] { "1.1", "run-a" })
        {
            var text = await world.Reader.BriefSessionAsync(
                await world.ScopeAsync(scope), "gate", WriteSeed.Keeper(fixture), Ct, signed: true);

            Assert.StartsWith($"REFUSED  work_brief — for:\"gate\" gates a phase, and {scope} is a ", text, StringComparison.Ordinal);
            Assert.Contains($"✗ {scope} is not a phase", text, StringComparison.Ordinal);
            Assert.Contains($"→ work_brief(scope:\"{scope}\") for the session payload", text, StringComparison.Ordinal);
            Assert.Contains("work_tree(scope:\"run-a\")", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GATE", text, StringComparison.Ordinal);
        }

        Assert.Equal(events, await WriteSeed.EventKindsAsync(world));
    }

    [Theory]
    [InlineData("review")]
    [InlineData("nonsense")]
    [InlineData("Audit")]
    public async Task A_rendering_that_is_not_built_is_refused_with_the_legal_list_and_never_falls_back(string variant)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var text = await ReadServer.CallAsync(
            client, "work_brief", new Dictionary<string, object?> { ["scope"] = "run-a", ["for"] = variant }, Ct);

        Assert.StartsWith($"REFUSED  work_brief — for:\"{variant}\" is not a rendering this server has.", text, StringComparison.Ordinal);
        Assert.Contains("for: takes \"resume\", \"worker\" or \"gate\"", text, StringComparison.Ordinal);
        Assert.Contains("→ work_brief(scope:\"run-a\") for the session payload", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN run-a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXT", text, StringComparison.Ordinal);

        foreach (var legal in new[] { "resume", "worker", "RESUME", "" })
        {
            var rendered = await ReadServer.CallAsync(
                client, "work_brief", new Dictionary<string, object?> { ["scope"] = "run-a", ["for"] = legal }, Ct);
            Assert.StartsWith("RUN run-a", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_ungated_phase_line_routes_to_a_gate_brief_that_opens_as_printed()
    {
        var world = await SeamWorldAsync();
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, (await world.ItemAsync("1.2")).Id, ItemStatus.Done);

        var attention = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);
        var gate = Assert.Single(attention, l => l.Rank == AttentionRank.PhaseGateNotRun);
        var call = Regex.Match(gate.Call, "^work_brief\\(scope:\"([^\"]+)\", for:\"(gate)\"\\)");
        Assert.True(call.Success, gate.Call);

        var brief = await world.Reader.BriefSessionAsync(
            await world.ScopeAsync(call.Groups[1].Value), call.Groups[2].Value, WriteSeed.Keeper(fixture), Ct, signed: true);
        Assert.StartsWith("GATE 1 — Foundations  ·  run run-a  ·  2 of 2 tasks done", brief, StringComparison.Ordinal);
    }

    /// <summary>gate-rest: <c>item.gate_criterion</c> is what must be true to accept the phase, and no query read it.</summary>
    [Fact]
    public async Task A_phases_gate_criterion_is_printed_in_the_gate_header_and_nothing_when_it_has_none()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var keeper = WriteSeed.Keeper(fixture);

        var without = await world.Reader.BriefSessionAsync(await world.ScopeAsync("1"), "gate", keeper, Ct, signed: true);
        Assert.DoesNotContain("PASS IF", without, StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            var phase = await db.Items.SingleAsync(i => i.Id == world.PhaseId, Ct);
            phase.GateCriterion = "every guard refuses with the call that fixes it, and the suite is green on a cold checkout";
            await db.SaveChangesAsync(Ct);
        }

        var with = await world.Reader.BriefSessionAsync(await world.ScopeAsync("1"), "gate", keeper, Ct, signed: true);
        var lines = with.Split('\n');
        var at = Array.FindIndex(lines, l => l.StartsWith("PASS IF  ", StringComparison.Ordinal));

        Assert.True(at > 0 && at < Array.IndexOf(lines, "TASKS (1)"), with);
        Assert.Equal(
            "every guard refuses with the call that fixes it, and the suite is green on a cold checkout",
            string.Join(" ", lines.Skip(at).TakeWhile(l => l.Length > 0).Select(l => l[9..].Trim())));
    }

    [Fact]
    public async Task The_addressed_header_says_which_call_consumes_and_whether_this_brief_did()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "for the task", "1.1", null, Ct)).Refused);
        var scope = await world.ScopeAsync("1.1");

        var resume = await world.Reader.BriefSessionAsync(scope, "resume", WriteSeed.Keeper(fixture), Ct, signed: true);
        Assert.Contains(
            "ADDRESSED TO YOU (1)   — only work_brief(scope:\"1.1\", for:\"worker\") consumes them; this brief did not\n",
            resume,
            StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            Assert.Null((await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).ConsumedAt);
        }

        var worker = await world.Reader.BriefSessionAsync(scope, "worker", WriteSeed.Keeper(fixture), Ct, signed: true);
        Assert.Contains(
            "ADDRESSED TO YOU (1)   — this worker brief marked those addressed to 1.1 consumed\n",
            worker,
            StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            Assert.NotNull((await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).ConsumedAt);
        }
    }
}

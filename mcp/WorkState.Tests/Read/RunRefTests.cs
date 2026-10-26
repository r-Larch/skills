using System.Text.RegularExpressions;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// runref. <c>work_get</c> on the run <c>autowork/backlog</c> printed <c>uri work://item/backlog</c>
/// and a bare Next while <c>m0/backlog</c> is a phase. A run numbers its own refs, so its
/// <c>QualifiedRef</c> is its bare ref and ws/10's qualify-when-shared rule printed the bare ref. A shared
/// run now prints the form the resolver accepts for it alone: parent-qualified.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RunRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Work_get_on_a_run_whose_ref_is_shared_prints_a_qualified_uri_and_next_that_round_trip()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = ProjectRefOf(world);
        await WriteSeed.SecondRunAsync(world, "backlog");
        await WriteSeed.ChildAsync(world, world.RunId, "backlog", kind: "phase");

        var home = await world.ScopeAsync("run-a");
        var text = await world.Reader.GetAsync(home, [$"{project}/backlog"], [], Ct);

        Assert.Contains($"  uri       work://item/{project}/backlog\n", text, StringComparison.Ordinal);
        Assert.Contains($"Next: work_status(ref:\"{project}/backlog\", to:\"in_progress\")", text, StringComparison.Ordinal);
        Assert.DoesNotContain("work://item/backlog\n", text, StringComparison.Ordinal);

        var printed = Regex.Match(text, @"uri       work://item/(\S+)").Groups[1].Value;
        var again = await world.Reader.GetAsync(home, [printed], [], Ct);

        Assert.DoesNotContain("✗", again, StringComparison.Ordinal);
        Assert.StartsWith("backlog ", again, StringComparison.Ordinal);
        Assert.Contains("The other run", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_whose_ref_is_unique_prints_its_bare_ref_and_a_shared_phase_stays_run_qualified()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = ProjectRefOf(world);
        await WriteSeed.SecondRunAsync(world, "backlog");
        await WriteSeed.ChildAsync(world, world.RunId, "backlog", kind: "phase");

        var home = await world.ScopeAsync("run-a");

        var run = await world.Reader.GetAsync(home, ["run-a"], [], Ct);
        Assert.Contains("  uri       work://item/run-a\n", run, StringComparison.Ordinal);

        var phase = await world.Reader.GetAsync(home, ["run-a/backlog"], [], Ct);
        Assert.Contains("  uri       work://item/run-a/backlog\n", phase, StringComparison.Ordinal);
        Assert.DoesNotContain($"{project}/backlog", phase, StringComparison.Ordinal);
    }

    /// <summary>closed-trailers. work_get's Next proposed work_status(in_progress) on a done item.</summary>
    [Fact]
    public async Task Work_get_of_a_done_item_proposes_no_move_and_an_open_item_still_proposes_starting_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.9", status: WorkState.Core.Domain.ItemStatus.Done);
        var home = await world.ScopeAsync("run-a");

        var done = await world.Reader.GetAsync(home, ["1.9"], [], Ct);
        Assert.DoesNotContain("work_status(", done, StringComparison.Ordinal);
        Assert.DoesNotContain("work_brief(", done, StringComparison.Ordinal);
        Assert.Contains("Next: work_next() for what is ready — 1.9 is already done;", done, StringComparison.Ordinal);

        var open = await world.Reader.GetAsync(home, ["1.1"], [], Ct);
        Assert.Contains("Next: work_status(ref:\"1.1\", to:\"in_progress\") to start it", open, StringComparison.Ordinal);
    }

    /// <summary>
    /// closed-trailers. A note addressed to a shared run printed <c>work_brief(scope:"backlog")</c>
    /// in attention and threads: the note query qualifies by the ref scope, which for a run is itself,
    /// so the "qualified" ref was the bare one and the call was refused as ambiguous.
    /// </summary>
    [Fact]
    public async Task A_note_addressed_to_a_shared_run_names_a_scope_that_resolves_to_that_run()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = ProjectRefOf(world);
        var task = await WriteSeed.SecondRunAsync(world, "backlog");
        await WriteSeed.ChildAsync(world, world.RunId, "backlog", kind: "phase");

        Guid run;
        await using (var db = fixture.NewContext())
        {
            run = db.Items.Single(i => i.Id == task).ParentId!.Value;
            db.Notes.Add(new WorkState.Core.Domain.Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                FromItemId = world.TaskId,
                ToItemId = run,
                Kind = WorkState.Core.Domain.NoteKind.CarryForward,
                Body = "the backlog run must read this",
                CreatedBy = "test",
            });
            await db.SaveChangesAsync(Ct);
        }

        var scope = await world.ScopeAsync(project);
        var attention = await world.Reader.AttentionAsync(scope, Ct);
        var note = Assert.Single(attention, l => l.Rank == WorkState.Core.Rendering.AttentionRank.UnconsumedNote);
        var threads = await world.Reader.ThreadsAsync(scope, ["notes"], Ct);

        var board = new WorkState.Core.Queries.BoardQueries(fixture.DataSource);
        foreach (var text in new[] { note.Call, threads })
        {
            var called = Regex.Match(text, @"work_brief\(scope:""([^""]+)"", for:""worker""\)");
            Assert.True(called.Success, text);

            var found = await board.ResolveAsync(world.WorkspaceId, called.Groups[1].Value, Ct);
            Assert.True(found.Count == 1, $"\"{called.Groups[1].Value}\" resolves to {found.Count} items:\n{text}");
            Assert.Equal(run, found[0].Id);
        }
    }

    private string ProjectRefOf(WriteWorld world)
    {
        using var db = fixture.NewContext();
        return db.Items.Single(i => i.Id == world.ProjectId).Ref;
    }
}

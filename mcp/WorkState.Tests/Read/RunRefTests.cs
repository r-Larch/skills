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

    private string ProjectRefOf(WriteWorld world)
    {
        using var db = fixture.NewContext();
        return db.Items.Single(i => i.Id == world.ProjectId).Ref;
    }
}

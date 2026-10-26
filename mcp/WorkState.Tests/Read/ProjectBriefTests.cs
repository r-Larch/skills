using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// doc 06 §4a — <c>work_brief</c> on a project is the continue-here context of its repository:
/// open runs with their next action, recent closed runs, the backlog, the project's rules and
/// notes, and a NEXT that resumes a run or starts one. Nobody works on a project, so nothing in it
/// transitions the project.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectBriefTests(PostgresFixture fixture)
{
    private const string Goal =
        "Every agent that opens this repository learns, from one call, what the project is for, which "
        + "runs are under way and what each needs next, what finished recently with which commit, and "
        + "what is waiting in the backlog — without being told a ref.";

    private const string LongNote =
        "The import of the legacy ledger must be replayed from the committed fixture, never from the live "
        + "corpus, because the live corpus grew from 181 to 182 files during a build and a gate against it "
        + "fails for reasons unrelated to the renderer; carry this into every run of the project.";

    private const string SecondNote =
        "Postgres is the only store: a second cache in front of it was measured and rejected because "
        + "invalidating it on every work_status cost more than the reads it saved, so do not re-derive it.";

    [Fact]
    public async Task A_project_brief_shows_open_runs_recent_runs_backlog_rules_notes_and_resumes_the_busiest_run()
    {
        var (world, project) = await FixtureAsync();

        var text = await BriefAsync(world, project);
        var flat = Glyph.Flat(text);
        var lines = text.Split('\n');

        Assert.Equal(
            $"PROJECT {project}  ·  The project under test  ·  repo remote github.com/r-larch/brief, root p:/projects/brief",
            lines[0]);
        Assert.Contains($"GOAL {Goal}", flat, StringComparison.Ordinal);

        Assert.Contains("OPEN RUNS (2)", lines);
        var runA = Array.IndexOf(lines, "  run-a — The run");
        var runB = Array.IndexOf(lines, "  run-b — The other run");
        Assert.True(runA > 0 && runB > runA, text);
        Assert.Equal("    0/2 tasks done · held: 1.2 by aw-alpha", lines[runA + 1]);
        Assert.Equal("    next  work_get(refs:[\"1.1\"]) then work_status(ref:\"1.1\", to:\"in_progress\")", lines[runA + 2]);
        Assert.Equal("    0/1 tasks done", lines[runB + 1]);
        Assert.Equal("    next  nothing ready — 9.1 is blocked; work_threads(scope:\"run-b\", include:\"deps\")", lines[runB + 2]);
        Assert.DoesNotContain(lines, l => l.StartsWith("  backlog — ", StringComparison.Ordinal));

        Assert.Contains("RECENT (5 of 7 closed)", lines);
        Assert.Contains(lines, l => l.StartsWith("  m7 — Closed run 7 — done/accepted · ", StringComparison.Ordinal)
                                    && l.EndsWith(" · c0ffee07", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  m6 — Closed run 6 — cancelled · ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  m3 — ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("  m2 — ", StringComparison.Ordinal));
        Assert.Contains(
            $"  · +2 older — work_tree(scope:\"{project}\", depth:1, status:\"done,cancelled,superseded\")",
            lines);

        Assert.Contains("BACKLOG (6 open)", lines);
        Assert.Contains("  b1     foll   Backlog follow-up 1", lines);
        Assert.Contains(
            $"  · +1 more — work_tree(scope:\"backlog\") and work_threads(scope:\"{project}\", include:\"followups\")",
            lines);

        Assert.Contains("BINDING (1)", lines);
        Assert.Contains("! C1 Never push from a worker — commit on the unit branch and report DONE", flat, StringComparison.Ordinal);

        Assert.Contains("NOTES IN SCOPE (2)", flat, StringComparison.Ordinal);
        Assert.Contains(LongNote, flat, StringComparison.Ordinal);
        Assert.Contains(SecondNote, flat, StringComparison.Ordinal);

        Assert.Contains(lines, l => l.StartsWith("LAST VERIFY  dotnet test --filter Guard → Passed!", StringComparison.Ordinal));
        Assert.Contains("NEXT   resume run run-a — work_brief(scope:\"run-a\")", lines);
    }

    [Fact]
    public async Task A_project_brief_never_proposes_a_transition_of_the_project()
    {
        var (world, project) = await FixtureAsync();
        var busy = await BriefAsync(world, project);

        var idle = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(idle, idle.RunId, ItemStatus.Done);
        var idleProject = await ProjectRefAsync(idle);
        var empty = await BriefAsync(idle, idleProject);

        Assert.DoesNotContain($"work_status(ref:\"{project}\"", busy, StringComparison.Ordinal);
        Assert.DoesNotContain($"work_status(ref:\"{idleProject}\"", empty, StringComparison.Ordinal);
        Assert.DoesNotContain(busy.Split('\n'), l => l.StartsWith("NEXT   work_status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_with_no_open_run_says_to_start_one()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.RunId, ItemStatus.Done);
        var project = await ProjectRefAsync(world);

        var lines = (await BriefAsync(world, project)).Split('\n');

        Assert.Contains("OPEN RUNS (0)", lines);
        Assert.Contains(
            $"NEXT   start a run under this project — work_create(items:[{{kind:\"run\", parent:\"{project}\", ref:\"<slug>\", title:\"…\"}}])",
            lines);
        Assert.Contains(lines, l => l.StartsWith("  run-a — The run — done/accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_brief_is_unchanged_by_the_project_brief()
    {
        var (world, _) = await FixtureAsync();

        var text = await BriefAsync(world, "run-a");

        Assert.StartsWith("RUN run-a  ·  project ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OPEN RUNS", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The project brief's own budget, apart from the run brief's 65 lines / 1250 tokens: measured on
    /// this fixture at 49 lines and 696 tokens (attention block included), bounded at that plus ~15%.
    /// </summary>
    [Fact]
    public async Task A_project_brief_on_the_fixture_stays_inside_its_budget()
    {
        var (world, project) = await FixtureAsync();

        var text = await BriefAsync(world, project);
        var tokens = (text.Length + 3) / 4;
        var lines = text.Split('\n').Length;

        Assert.True(tokens <= 800, $"the project brief rendered {tokens} tokens ({text.Length} chars, {lines} lines)");
        Assert.True(lines <= 56, $"the project brief rendered {lines} lines");
    }

    private static async Task<string> BriefAsync(WriteWorld world, string scope)
        => await world.Reader.BriefAsync(await world.ScopeAsync(scope), "resume", Ct);

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    /// <summary>
    /// Two open runs — run-a with a ready task and a claimed one, run-b fully blocked — seven closed
    /// runs each with a commit, a backlog run plus follow-ups left under a closed run, one project
    /// rule, two long notes and a verification.
    /// </summary>
    private async Task<(WriteWorld World, string Project)> FixtureAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var runB = await WriteSeed.SecondRunAsync(world);
        await WriteSeed.StatusAsync(world, runB, ItemStatus.Blocked);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        await using var db = fixture.NewContext();
        var project = await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct);
        project.Title = "The project under test";
        project.Intent = Goal;

        var claimed = Seed.NewItem(world.WorkspaceId, world.PhaseId, "task", "1.2", 2m, "The claimed task");
        claimed.Status = ItemStatus.InProgress;
        claimed.StartedAt = DateTimeOffset.UtcNow;
        db.Items.Add(claimed);
        db.Claims.Add(new Claim
        {
            Id = Guid.CreateVersion7(),
            ItemId = claimed.Id,
            SerializationKey = $"tree-{world.RunId:n}",
            Writes = true,
            AgentName = "aw-alpha",
            Role = ActorRole.Orchestrator,
            AcquiredAt = DateTimeOffset.UtcNow,
            HeartbeatAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        });

        Item? newest = null;
        for (var n = 1; n <= 7; n++)
        {
            var done = n % 2 == 1;
            var closedAt = DateTimeOffset.UtcNow.AddDays(n - 10);
            var run = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", $"m{n}", 10m + n, $"Closed run {n}");
            run.Status = done ? ItemStatus.Done : ItemStatus.Cancelled;
            run.Resolution = done ? ItemResolution.Accepted : null;
            run.ClosedAt = closedAt;

            var task = Seed.NewItem(world.WorkspaceId, run.Id, "task", $"m{n}.1", 1m, $"Work of run {n}");
            task.Status = ItemStatus.Done;
            task.Resolution = ItemResolution.Accepted;
            task.ClosedAt = closedAt;

            db.Items.AddRange(run, task);
            db.Artifacts.Add(new Artifact
            {
                Id = Guid.CreateVersion7(),
                ItemId = task.Id,
                Kind = "commit",
                Value = $"c0ffee0{n}aaaabbbbccccddddeeeeffff00001111",
                At = closedAt,
            });
            newest = run;
        }

        var backlog = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", "backlog", 50m, WorkWriter.BacklogRunTitle);
        db.Items.Add(backlog);
        for (var n = 1; n <= 5; n++)
        {
            var item = Seed.NewItem(world.WorkspaceId, backlog.Id, "followup", $"b{n}", n, $"Backlog follow-up {n}");
            if (n == 5)
            {
                item.Status = ItemStatus.Done;
                item.Resolution = ItemResolution.Accepted;
                item.ClosedAt = DateTimeOffset.UtcNow;
            }

            db.Items.Add(item);
        }

        for (var n = 1; n <= 2; n++)
        {
            db.Items.Add(Seed.NewItem(world.WorkspaceId, newest!.Id, "followup", $"f{n}", 10m + n, $"Left over from run 7, number {n}"));
        }

        db.ConstraintRules.Add(new ConstraintRule
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ScopeItemId = world.ProjectId,
            Ref = "C1",
            Kind = ConstraintKind.RedLine,
            Text = "Never push from a worker",
            Route = "commit on the unit branch and report DONE",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.Notes.AddRange(
            new Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                FromItemId = world.ProjectId,
                Kind = NoteKind.CarryForward,
                Body = LongNote,
            },
            new Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                FromItemId = world.RunId,
                Kind = NoteKind.DoNotRederive,
                Body = SecondNote,
            });

        await db.SaveChangesAsync(Ct);

        var bound = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project.Ref,
            new UpdateArgs(Repo: new RepoArg("git@github.com:r-Larch/Brief.git", @"P:\Projects\Brief")), Ct);
        Assert.False(bound.Refused, bound.Text);

        return (world, project.Ref);
    }
}

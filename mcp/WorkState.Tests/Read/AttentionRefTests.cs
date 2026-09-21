using System.Text.Json;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Tests.App;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// U6.2 — every attention line says which item it is about, qualified, and the text an agent reads
/// does not move by one byte while it learns to. The two halves are asserted together on purpose:
/// the qualified ref exists for doc 09 §5's queue, and the moment it leaks into doc 06 §5's block it
/// has changed what every agent reads in order to improve a dashboard.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AttentionRefTests(PostgresFixture fixture)
{
    /// <summary>
    /// The block exactly as <c>work_brief</c> appends it. A golden, not a shape: doc 06 §5 fixes
    /// these bytes, and a field added beside the text is the only kind of change this unit may make.
    /// </summary>
    private static readonly string[] Rendered =
    [
        "--- attention (5) ---",
        "⚠ 1.2 claim by worker-1 expired 1h ago — work_status(ref:\"1.2\", to:\"in_progress\") to retake it",
        "⚠ F-07 (BLOCKER) is open against 1.2: ContentHash written early — work_status(ref:\"F-07\", to:\"done\")",
        "⚠ 2: all 1 tasks done, gate not run — work_brief(scope:\"2\") to run it",
        "⚠ 2.1 is done with no orchestrator verification — work_verify(ref:\"2.1\", result_line:\"…\", run_by:\"orchestrator\")",
        "⚠ 1.1 is done with no orchestrator verification — work_verify(ref:\"1.1\", result_line:\"…\", run_by:\"orchestrator\")",
    ];

    /// <summary>The three lines the five-line cap hides, so all seven sources have their text pinned.</summary>
    private static readonly string[] BelowTheCap =
    [
        "A3 is past revisit_when (1.1 is closed) — work_assume(ref:\"A3\", resolve:\"confirmed\", evidence:\"…\")",
        "1 note(s) addressed to 1.3 unconsumed — work_brief(scope:\"1.3\", for:\"worker\")",
        "1.3 has been in_progress 1d with no live claim — work_status(ref:\"1.3\", to:\"in_progress\") to retake it",
    ];

    [Fact]
    public async Task The_rendered_attention_block_is_byte_identical_for_a_world_that_fires_every_source()
    {
        var world = await SevenSourcesAsync();
        var reader = Reader();
        var lines = await reader.AttentionAsync(await ScopeAsync(reader, world.Slug, "run-one"), Ct);

        Assert.Equal(string.Join("\n", Rendered), Attention.Block(lines));

        Assert.Equal(
            string.Join("\n", BelowTheCap),
            string.Join("\n", lines
                .Where(l => l.Rank >= AttentionRank.AssumptionPastRevisit)
                .OrderBy(l => l.Rank)
                .Select(l => $"{l.What} — {l.Call}")));
    }

    /// <summary>
    /// Every source names its item in the ref scope that owns the ref — the run, never the parent.
    /// <c>2.1</c> sits under phase <c>2</c>, so a parent-qualified line would read <c>2/2.1</c> and
    /// point at nothing; <c>F-07</c> hangs off task <c>1.2</c> and would read <c>1.2/F-07</c>.
    /// </summary>
    [Fact]
    public async Task Every_attention_source_qualifies_its_ref_with_the_ref_scope_that_owns_it()
    {
        var world = await SevenSourcesAsync();
        var reader = Reader();
        var lines = await reader.AttentionAsync(await ScopeAsync(reader, world.Slug, "run-one"), Ct);

        Assert.All(lines, l => Assert.NotNull(l.QualifiedRef));

        Assert.Equal(
            "run-one/1.2, run-one/F-07, run-one/2, run-one/2.1, run-one/1.1, "
            + "run-one/A3, run-one/1.3, run-one/1.3",
            string.Join(", ", lines.OrderBy(l => l.Rank).Select(l => l.QualifiedRef)));
    }

    /// <summary>
    /// The defect this unit exists for, measured on the live corpus: <c>phase-4</c> is a phase in
    /// four different runs, and the queue could not say which. Two runs is enough to prove it — a
    /// bare ref collides, a qualified one does not.
    /// </summary>
    [Fact]
    public async Task The_same_bare_ref_under_two_runs_gives_two_rows_with_different_qualified_refs()
    {
        var world = await TwoRunsAsync();

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var client = await server.SignInAsync(Ct);

        var watch = await AppServer.ReadJsonAsync(client, "/api/watch", Ct);
        var needsYou = watch.GetProperty("needsYou").EnumerateArray().ToList();

        foreach (var (reference, rank) in ((string Ref, string Rank)[])
                 [("phase-4", "phase_gate_not_run"), ("4.1", "done_unverified")])
        {
            var rows = needsYou
                .Where(a => a.GetProperty("ref").GetString() == reference
                            && a.GetProperty("rank").GetString() == rank)
                .Select(a => a.GetProperty("qualifiedRef").GetString() ?? "(null)")
                .ToList();

            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Distinct().Count());
            Assert.Equal(
                $"run-one/{reference}, run-two/{reference}",
                string.Join(", ", rows.Order()));
        }
    }

    /// <summary>
    /// The one line whose subject is not an item: it is addressed to a ref, and at project scope
    /// that ref names two items. Null says so; falling back to the bare ref would restate exactly
    /// the ambiguity the field was added to remove.
    /// </summary>
    [Fact]
    public async Task An_unconsumed_note_group_spanning_two_runs_has_no_qualified_ref_of_its_own()
    {
        var world = await TwoRunsAsync();
        var reader = Reader();
        var lines = await reader.AttentionAsync(await ScopeAsync(reader, world.Slug, "proj-q"), Ct);

        var note = Assert.Single(lines, l => l.Rank == AttentionRank.UnconsumedNote);

        Assert.Equal("4.1", note.Ref);
        Assert.Null(note.QualifiedRef);
        Assert.Equal("2 note(s) addressed to 4.1 unconsumed", note.What);
    }

    /// <summary>
    /// doc 09 §5's honesty strip counts rows, and <c>DoneUnverifiedAsync</c> returning a row type
    /// rather than a string must not change the number. The count is still the list's length.
    /// </summary>
    [Fact]
    public async Task The_honesty_count_is_still_the_length_of_the_done_unverified_list()
    {
        var world = await TwoRunsAsync();
        var board = new BoardQueries(fixture.DataSource);
        var reader = Reader();

        var expected = (await board.DoneUnverifiedAsync(
            (await ScopeAsync(reader, world.Slug, "proj-q")).Item.Id, Ct)).Count;

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var client = await server.SignInAsync(Ct);

        var watch = await AppServer.ReadJsonAsync(client, "/api/watch", Ct);

        Assert.Equal(2, expected);
        Assert.Equal(expected, watch.GetProperty("honesty").GetProperty("doneUnverified").GetInt32());
    }

    private BoardReader Reader()
        => new(new WorkStateQueries(fixture.DataSource), new BoardQueries(fixture.DataSource));

    private static async Task<ScopeResult.Found> ScopeAsync(BoardReader reader, string slug, string reference)
        => (ScopeResult.Found)await reader.ResolveAsync(slug, reference, Ct);

    private sealed record World(string Slug);

    /// <summary>
    /// One run that trips all seven sources of <c>AttentionAsync</c> at once, with every instant far
    /// enough inside its <see cref="Glyph.Age"/> band that the rendered text is the same minute to
    /// minute: 90 minutes reads <c>1h</c> until it is two hours old, 30 hours reads <c>1d</c>.
    /// </summary>
    private async Task<World> SevenSourcesAsync()
    {
        var slug = $"attref-{Guid.CreateVersion7():n}";
        var now = DateTimeOffset.UtcNow;

        await using var db = fixture.NewContext();

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = slug };
        db.Workspaces.Add(workspace);

        var project = Seed.NewItem(workspace.Id, null, "project", "proj-q", 1m, "Project Q");
        db.Items.Add(project);
        await db.SaveChangesAsync(Ct);

        var run = Seed.NewItem(workspace.Id, project.Id, "run", "run-one", 1m, "The one run");
        db.Items.Add(run);
        await db.SaveChangesAsync(Ct);

        var one = Seed.NewItem(workspace.Id, run.Id, "phase", "1", 1m, "Foundations");
        var two = Seed.NewItem(workspace.Id, run.Id, "phase", "2", 2m, "Ingestion");
        db.Items.AddRange(one, two);
        await db.SaveChangesAsync(Ct);

        var t11 = Seed.NewItem(workspace.Id, one.Id, "task", "1.1", 1m, "First task");
        t11.Status = ItemStatus.Done;
        t11.Resolution = ItemResolution.Accepted;
        t11.ClosedAt = now.AddHours(-3);

        var t12 = Seed.NewItem(workspace.Id, one.Id, "task", "1.2", 2m, "Second task");

        var t13 = Seed.NewItem(workspace.Id, one.Id, "task", "1.3", 3m, "Third task");
        t13.Status = ItemStatus.InProgress;
        t13.StartedAt = now.AddHours(-30);

        var t21 = Seed.NewItem(workspace.Id, two.Id, "task", "2.1", 1m, "The only task of phase two");
        t21.Status = ItemStatus.Done;
        t21.Resolution = ItemResolution.Accepted;
        t21.ClosedAt = now.AddHours(-1);

        db.Items.AddRange(t11, t12, t13, t21);
        await db.SaveChangesAsync(Ct);

        var finding = Seed.NewItem(workspace.Id, t12.Id, "finding", "F-07", 1m, "ContentHash written early");
        finding.Severity = "blocker";
        finding.Source = "review";
        db.Items.Add(finding);
        await db.SaveChangesAsync(Ct);

        db.Claims.Add(new Claim
        {
            Id = Guid.CreateVersion7(),
            ItemId = t12.Id,
            SerializationKey = slug,
            Writes = true,
            AgentName = "worker-1",
            Role = ActorRole.Worker,
            ExpiresAt = now.AddMinutes(-90),
        });

        db.Assumptions.Add(new Assumption
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            ScopeItemId = run.Id,
            Ref = "A3",
            Statement = "The dev machine can reach the export endpoint",
            Evidence = EvidenceLevel.Reasoned,
            Reversal = ReversalCost.Medium,
            RevisitItemId = t11.Id,
            State = AssumptionState.Open,
        });

        db.Notes.Add(new Note
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            FromItemId = run.Id,
            ToItemId = t13.Id,
            Kind = NoteKind.CarryForward,
            Body = "truncate facet values to 512 before insert",
            CreatedBy = "test",
        });

        await db.SaveChangesAsync(Ct);
        return new World(slug);
    }

    /// <summary>
    /// The live corpus in miniature: the same <c>phase-4</c>, and the same <c>4.1</c> beneath it, in
    /// two runs under one project — which is the scope <c>/api/watch</c> reads.
    /// </summary>
    private async Task<World> TwoRunsAsync()
    {
        var slug = $"attref-{Guid.CreateVersion7():n}";
        var now = DateTimeOffset.UtcNow;

        await using var db = fixture.NewContext();

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = slug };
        db.Workspaces.Add(workspace);

        var project = Seed.NewItem(workspace.Id, null, "project", "proj-q", 1m, "Project Q");
        db.Items.Add(project);
        await db.SaveChangesAsync(Ct);

        var one = Seed.NewItem(workspace.Id, project.Id, "run", "run-one", 1m, "Content store");
        var two = Seed.NewItem(workspace.Id, project.Id, "run", "run-two", 2m, "Document store");
        db.Items.AddRange(one, two);
        await db.SaveChangesAsync(Ct);

        foreach (var (run, closed) in (( Item Run, DateTimeOffset Closed )[])[(one, now.AddHours(-2)), (two, now.AddHours(-1))])
        {
            await using var inner = fixture.NewContext();

            var phase = Seed.NewItem(workspace.Id, run.Id, "phase", "phase-4", 4m, "Phase four");
            inner.Items.Add(phase);
            await inner.SaveChangesAsync(Ct);

            var task = Seed.NewItem(workspace.Id, phase.Id, "task", "4.1", 1m, "The colliding task");
            task.Status = ItemStatus.Done;
            task.Resolution = ItemResolution.Accepted;
            task.ClosedAt = closed;
            inner.Items.Add(task);
            await inner.SaveChangesAsync(Ct);

            inner.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.Id,
                FromItemId = run.Id,
                ToItemId = task.Id,
                Kind = NoteKind.CarryForward,
                Body = "the facet cap applies here too",
                CreatedBy = "test",
            });

            await inner.SaveChangesAsync(Ct);
        }

        return new World(slug);
    }
}

using Dapper;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.AgentImport;

/// <summary>
/// work_import's writer: a batch of what an agent judged still true of a repo, written through the
/// same guards as work_create and the thread writers, keyed by source so a re-import updates.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ImportWriterTests(PostgresFixture fixture)
{
    private static ImportSourceArg At(string anchor) => new("docs/plan.md", anchor);

    private static ImportItemArg[] EveryKind() =>
    [
        new(At("## Import"), "run", "the import tool is not built yet", Title: "Agent import", Ref: "imp"),
        new(At("## Import / server"), "phase", "the server half is open", Title: "Server", Parent: "imp"),
        new(At("## Import / server / tool"), "task", "WorkWriter has no import yet", Title: "work_import tool",
            Parent: "imp/1", VerifyCmd: "dotnet test --filter Import", Criteria: ["imports", "re-imports"], Status: "ready"),
        new(At("## Import / docs"), "followup", "docs/workstate says nothing of it", Title: "Document the import", Parent: "imp"),
        new(At("## Import / note"), "note", "still how the code reads", Body: "The parser in tools/ is not used.", Parent: "imp"),
        new(At("## Import / assumption"), "assumption", "nothing contradicts it yet", Body: "One project per repo holds."),
        new(At("## Import / rule"), "rule", "AGENTS.md still says so", Body: "Never bump plugin.json for mcp/.", Route: "ask the controller"),
    ];

    [Fact]
    public async Task Every_kind_is_written_with_its_source_and_a_parent_created_earlier_in_the_batch_resolves()
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world, EveryKind());

        Assert.False(result.Refused, result.Text);
        Assert.StartsWith($"OK  project {await ProjectRefAsync(world)} — 7 created, 0 updated, 0 skipped", result.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var run = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "imp", Ct);
        var phase = await db.Items.SingleAsync(i => i.ParentId == run.Id && i.Kind == "phase", Ct);
        var task = await db.Items.SingleAsync(i => i.ParentId == phase.Id, Ct);
        var followup = await db.Items.SingleAsync(i => i.ParentId == run.Id && i.Kind == "followup", Ct);

        Assert.Equal(world.ProjectId, run.ParentId);
        Assert.Equal("import:docs/plan.md### Import / server / tool", task.Source);
        Assert.Equal(ItemStatus.Ready, task.Status);
        Assert.Equal(ItemStatus.Open, followup.Status);
        Assert.Equal(["imports", "re-imports"], await db.ItemCriteria.Where(c => c.ItemId == task.Id).OrderBy(c => c.Seq).Select(c => c.Text).ToListAsync(Ct));

        var note = await db.Notes.SingleAsync(n => n.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal(run.Id, note.ToItemId);
        Assert.Equal(NoteKind.CarryForward, note.Kind);

        var assumption = await db.Assumptions.SingleAsync(a => a.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal(world.ProjectId, assumption.ScopeItemId);
        Assert.Equal("import:docs/plan.md### Import / assumption — nothing contradicts it yet", assumption.EvidenceNote);

        var rule = await db.ConstraintRules.SingleAsync(c => c.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal(ConstraintKind.RedLine, rule.Kind);
        Assert.Equal("ask the controller", rule.Route);

        var records = await db.ImportRecords.Where(r => r.ProjectItemId == world.ProjectId).ToListAsync(Ct);
        Assert.Equal(7, records.Count);
        Assert.All(records, r => Assert.Equal("docs/plan.md", r.SourcePath));

        // Nothing an import writes can satisfy a done guard: no verification, no done.
        Assert.False(await db.Verifications.AnyAsync(v => v.ItemId == task.Id, Ct));
        Assert.Equal(7, await db.Events.CountAsync(e => e.WorkspaceId == world.WorkspaceId && e.Kind == "import", Ct));
    }

    [Fact]
    public async Task A_second_import_of_the_same_sources_updates_and_does_not_duplicate()
    {
        var world = await BoundWorldAsync();
        Assert.False((await ImportAsync(world, EveryKind())).Refused);

        var again = EveryKind();
        again[2] = again[2] with { Title = "work_import tool, renamed", Criteria = ["imports once"] };
        again[4] = again[4] with { Body = "The parser in tools/ stays unused." };

        var result = await ImportAsync(world, again);

        Assert.False(result.Refused, result.Text);
        Assert.Contains("0 created, 7 updated, 0 skipped", result.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Equal(7, await db.ImportRecords.CountAsync(r => r.ProjectItemId == world.ProjectId, Ct));
        Assert.Equal(1, await db.Items.CountAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "imp", Ct));
        var task = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Title == "work_import tool, renamed", Ct);
        Assert.Equal(["imports once"], await db.ItemCriteria.Where(c => c.ItemId == task.Id).Select(c => c.Text).ToListAsync(Ct));
        Assert.Equal("The parser in tools/ stays unused.", (await db.Notes.SingleAsync(n => n.WorkspaceId == world.WorkspaceId, Ct)).Body);
    }

    [Fact]
    public async Task An_item_moved_since_import_is_skipped_and_reported_and_left_as_it_is()
    {
        var world = await BoundWorldAsync();
        Assert.False((await ImportAsync(world, EveryKind())).Refused);

        Guid taskId;
        await using (var db = fixture.NewContext())
        {
            taskId = (await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Title == "work_import tool", Ct)).Id;
        }

        await WriteSeed.StatusAsync(world, taskId, ItemStatus.Blocked);

        var again = EveryKind();
        again[2] = again[2] with { Title = "renamed after it moved", Status = "open" };

        var result = await ImportAsync(world, again);

        Assert.False(result.Refused, result.Text);
        Assert.Contains("0 created, 6 updated, 1 skipped", result.Text, StringComparison.Ordinal);
        Assert.Contains("moved since import (now blocked)", result.Text, StringComparison.Ordinal);

        await using var after = fixture.NewContext();
        var task = await after.Items.SingleAsync(i => i.Id == taskId, Ct);
        Assert.Equal("work_import tool", task.Title);
        Assert.Equal(ItemStatus.Blocked, task.Status);
    }

    [Fact]
    public async Task Status_done_is_refused_and_nothing_is_written()
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world,
        [
            new(At("## Shipped"), "followup", "it shipped", Title: "Already done", Status: "done"),
        ]);

        Assert.True(result.Refused);
        RefusalShape.SelfCorrecting(result.Text);
        Assert.Contains("there is no imported done", result.Text, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(world);
    }

    [Fact]
    public async Task Dry_run_shows_what_would_be_written_and_writes_nothing()
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world, EveryKind(), dryRun: true);

        Assert.False(result.Refused, result.Text);
        Assert.StartsWith("DRY RUN", result.Text, StringComparison.Ordinal);
        Assert.Contains("would leave 7 created, 0 updated, 0 skipped", result.Text, StringComparison.Ordinal);
        Assert.Contains("work_import tool", result.Text, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(world);
    }

    [Fact]
    public async Task A_task_without_a_verify_cmd_is_refused_as_work_create_refuses_it_and_the_batch_rolls_back()
    {
        var world = await BoundWorldAsync();

        var result = await ImportAsync(world,
        [
            new(At("## Run"), "run", "open", Title: "A run", Ref: "r"),
            new(At("## Run / task"), "task", "open", Title: "No command", Parent: "r"),
        ]);

        Assert.True(result.Refused);
        RefusalShape.SelfCorrecting(result.Text);
        Assert.Contains("kind:\"followup\"", result.Text, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(world);
    }

    [Fact]
    public async Task A_missing_or_unbound_project_is_refused_with_the_call_that_fixes_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        ImportItemArg[] one = [new(At("## X"), "followup", "open", Title: "X")];

        var missing = await world.Writer.ImportAsync(world.WorkspaceId, WriteSeed.Orchestrator, "no-such", one, false, Ct);
        Assert.True(missing.Refused);
        RefusalShape.SelfCorrecting(missing.Text);
        Assert.Contains("work_brief(repo:", missing.Text, StringComparison.Ordinal);

        var unbound = await world.Writer.ImportAsync(world.WorkspaceId, WriteSeed.Orchestrator, project, one, false, Ct);
        Assert.True(unbound.Refused);
        RefusalShape.SelfCorrecting(unbound.Text);
        Assert.Contains($"work_update(ref:\"{project}\", repo:", unbound.Text, StringComparison.Ordinal);

        await AssertNothingWrittenAsync(world);
    }

    [Fact]
    public async Task Work_get_prints_an_imported_items_source_and_why_whole()
    {
        var world = await BoundWorldAsync();
        var why = string.Join(' ', Enumerable.Repeat("the code in WorkWriter.Import.cs still bears this out", 6));
        Assert.False((await ImportAsync(world,
        [
            new(At("## A heading that is long enough to wrap the line it is printed on"), "followup", why, Title: "Imported", Ref: "imp-f"),
        ])).Refused);

        await using var db = fixture.NewContext();
        var followup = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Title == "Imported", Ct);

        var text = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), [followup.Ref], [], Ct);
        var flat = string.Join(' ', text.Split('\n').Select(l => l.Trim()));

        Assert.Contains("imported  from docs/plan.md### A heading", text, StringComparison.Ordinal);
        Assert.Contains(why, flat, StringComparison.Ordinal);
    }

    private async Task<WriteWorld> BoundWorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var bound = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            await ProjectRefAsync(world),
            new UpdateArgs(Repo: new RepoArg($"git@github.com:r-larch/import-{world.WorkspaceId:n}.git")),
            Ct);
        Assert.False(bound.Refused, bound.Text);
        return world;
    }

    private async Task<WriteResult> ImportAsync(WriteWorld world, ImportItemArg[] items, bool dryRun = false)
        => await world.Writer.ImportAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, await ProjectRefAsync(world), items, dryRun, Ct);

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private async Task AssertNothingWrittenAsync(WriteWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            """
            SELECT (SELECT count(*) FROM import_record WHERE workspace_id = @w)
                 + (SELECT count(*) FROM item WHERE workspace_id = @w AND source LIKE 'import:%')
                 + (SELECT count(*) FROM note WHERE workspace_id = @w)
                 + (SELECT count(*) FROM assumption WHERE workspace_id = @w)
                 + (SELECT count(*) FROM constraint_rule WHERE workspace_id = @w)
                 + (SELECT count(*) FROM event WHERE workspace_id = @w AND kind = 'import');
            """,
            new { w = world.WorkspaceId }));
    }
}

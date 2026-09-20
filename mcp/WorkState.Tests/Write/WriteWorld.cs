using Microsoft.EntityFrameworkCore;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// A workspace of its own, with a project, a run that owns a working tree, a phase and a task that
/// carries a verification command. Seeded directly, because a guard does not need work_create to be
/// exercised — and a guard tested through the tool that creates its own fixture proves less.
/// </summary>
internal sealed record WriteWorld(
    PostgresFixture Fixture,
    string Slug,
    Guid WorkspaceId,
    Guid ProjectId,
    Guid RunId,
    Guid PhaseId,
    Guid TaskId)
{
    public WorkWriter Writer => WriteSeed.Writer(Fixture);

    public BoardReader Reader => new(
        new WorkStateQueries(Fixture.DataSource),
        new BoardQueries(Fixture.DataSource));

    public async Task<ScopeResult.Found> ScopeAsync(string reference)
        => (ScopeResult.Found)await Reader.ResolveAsync(Slug, reference, Ct);

    public async Task<Item> ItemAsync(string reference)
    {
        await using var db = Fixture.NewContext();
        return await db.Items.FirstAsync(i => i.WorkspaceId == WorkspaceId && i.Ref == reference, Ct);
    }

    public async Task<ItemStatus> StatusAsync(string reference) => (await ItemAsync(reference)).Status;
}

internal static class WriteSeed
{
    public static readonly Actor Orchestrator = new("orchestrator-a", ActorRole.Orchestrator);
    public static readonly Actor Other = new("orchestrator-b", ActorRole.Orchestrator);
    public static readonly Actor Worker = new("worker-1", ActorRole.Worker);

    public static WorkWriter Writer(PostgresFixture fixture)
        => new(fixture.DataSource, new BoardQueries(fixture.DataSource), new Housekeeping(fixture.DataSource));

    public static Housekeeping Keeper(PostgresFixture fixture) => new(fixture.DataSource);

    public static async Task<WriteWorld> CreateAsync(PostgresFixture fixture, string? serializationKey = null)
    {
        await using var db = fixture.NewContext();

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = $"w-{Guid.CreateVersion7():n}" };
        db.Workspaces.Add(workspace);

        var project = Seed.NewItem(workspace.Id, null, "project", $"p{Guid.CreateVersion7():n}"[..10], 1m);
        var run = Seed.NewItem(workspace.Id, project.Id, "run", "run-a", 1m, "The run");
        var phase = Seed.NewItem(workspace.Id, run.Id, "phase", "1", 1m, "Foundations");
        var second = Seed.NewItem(workspace.Id, run.Id, "phase", "2", 2m, "Ingestion");
        var task = Seed.NewItem(workspace.Id, phase.Id, "task", "1.1", 1m, "The task under test");
        task.VerifyCmd = "dotnet test --filter Guard";

        db.Items.AddRange(project, run, phase, second, task);
        await db.SaveChangesAsync(Ct);

        db.RunConfigs.Add(new RunConfig
        {
            ItemId = run.Id,
            ProjectItemId = project.Id,
            SerializationKey = serializationKey ?? $"tree-{run.Id:n}",
        });

        await db.SaveChangesAsync(Ct);

        return new WriteWorld(
            fixture, workspace.Slug, workspace.Id, project.Id, run.Id, phase.Id, task.Id);
    }

    /// <summary>A second run in the same workspace, on a working tree of its own.</summary>
    public static async Task<Guid> SecondRunAsync(WriteWorld world, string reference = "run-b")
    {
        await using var db = world.Fixture.NewContext();

        var run = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", reference, 9m, "The other run");
        var task = Seed.NewItem(world.WorkspaceId, run.Id, "task", "9.1", 1m, "A task over there");
        task.VerifyCmd = "dotnet test --filter Other";
        db.Items.AddRange(run, task);
        await db.SaveChangesAsync(Ct);

        db.RunConfigs.Add(new RunConfig
        {
            ItemId = run.Id,
            ProjectItemId = world.ProjectId,
            SerializationKey = $"other-tree-{run.Id:n}",
        });

        await db.SaveChangesAsync(Ct);
        return task.Id;
    }

    public static async Task CriterionAsync(WriteWorld world, Guid itemId, int seq, string text, bool ticked)
    {
        await using var db = world.Fixture.NewContext();
        db.ItemCriteria.Add(new ItemCriterion
        {
            Id = Guid.CreateVersion7(),
            ItemId = itemId,
            Seq = seq,
            Text = text,
            Checked = ticked,
            CheckedAt = ticked ? DateTimeOffset.UtcNow : null,
        });

        await db.SaveChangesAsync(Ct);
    }

    public static async Task VerificationAsync(
        WriteWorld world,
        Guid itemId,
        VerifyRunner runBy,
        string? command = "dotnet test --filter Guard",
        bool passed = true)
    {
        await using var db = world.Fixture.NewContext();
        db.Verifications.Add(new Verification
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ItemId = itemId,
            Command = command,
            ResultLine = passed ? "Passed! Failed: 0, Passed: 91" : "Failed! Failed: 3, Passed: 88",
            Passed = passed,
            RunBy = runBy,
        });

        await db.SaveChangesAsync(Ct);
    }

    public static async Task<Guid> ChildAsync(
        WriteWorld world,
        Guid parentId,
        string reference,
        string kind = "task",
        string? severity = null,
        ItemStatus status = ItemStatus.Open)
    {
        await using var db = world.Fixture.NewContext();

        var seq = await db.Items.Where(i => i.ParentId == parentId).CountAsync(Ct) + 10;
        var child = Seed.NewItem(world.WorkspaceId, parentId, kind, reference, seq, $"child {reference}");
        child.Severity = severity;
        child.Status = status;

        if (status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded)
        {
            child.ClosedAt = DateTimeOffset.UtcNow;
            child.Resolution = status == ItemStatus.Done ? ItemResolution.Accepted : null;
        }

        db.Items.Add(child);
        await db.SaveChangesAsync(Ct);
        return child.Id;
    }

    public static async Task StatusAsync(WriteWorld world, Guid itemId, ItemStatus status)
    {
        await using var db = world.Fixture.NewContext();
        var item = await db.Items.FirstAsync(i => i.Id == itemId, Ct);
        item.Status = status;
        item.StartedAt ??= DateTimeOffset.UtcNow;

        if (status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded)
        {
            item.ClosedAt = DateTimeOffset.UtcNow;
            item.Resolution = status == ItemStatus.Done ? ItemResolution.Accepted : null;
        }
        else
        {
            item.ClosedAt = null;
            item.Resolution = null;
        }

        await db.SaveChangesAsync(Ct);
    }

    public static async Task LinkAsync(WriteWorld world, Guid fromId, Guid toId, LinkType type)
    {
        await using var db = world.Fixture.NewContext();
        db.ItemLinks.Add(new ItemLink
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            FromItemId = fromId,
            ToItemId = toId,
            Type = type,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Back-dates a live claim so the sweep has something real to find.</summary>
    public static async Task ExpireClaimAsync(WriteWorld world, Guid itemId)
    {
        await using var db = world.Fixture.NewContext();
        var claim = await db.Claims.FirstAsync(c => c.ItemId == itemId && c.ReleasedAt == null, Ct);
        claim.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-13);
        claim.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-43);
        await db.SaveChangesAsync(Ct);
    }

    public static async Task<IReadOnlyList<string>> EventKindsAsync(WriteWorld world, Guid? itemId = null)
    {
        await using var db = world.Fixture.NewContext();
        return await db.Events
            .Where(e => e.WorkspaceId == world.WorkspaceId && (itemId == null || e.ItemId == itemId))
            .OrderBy(e => e.Seq)
            .Select(e => e.Kind)
            .ToListAsync(Ct);
    }
}

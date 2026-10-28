using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Migrations;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// m56. A project or a run is its own ref scope, so neither the free-ref probe nor item_ref_unique ever
/// compared it with its siblings: every ref-less run was <c>1</c>, every ref-less project <c>project-1</c>,
/// and two runs <c>1</c> under one project both inserted. Siblings are now numbered and kept unique among
/// themselves, within one workspace.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ScopeRefTests(PostgresFixture fixture)
{
    private const string BeforeScopeRefUnique = "20261028204446_TenantRls";

    [Fact]
    public async Task ScopeRef_ref_less_runs_number_among_their_project_siblings()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var seeded = await SeededProjectAsync(world);
        var other = await ProjectAsync(world, "other");

        Assert.Equal(["1"], await CreatedRefsAsync(world, Run(seeded, "first")));
        Assert.Equal(["2", "3"], await CreatedRefsAsync(world, Run(seeded, "second"), Run(seeded, "third")));
        Assert.Equal(["1"], await CreatedRefsAsync(world, Run("other", "elsewhere")));

        await using var db = fixture.NewContext();
        Assert.Equal(
            ["1", "2", "3", "run-a"],
            await db.Items.Where(i => i.ParentId == world.ProjectId && i.Kind == "run").Select(i => i.Ref).OrderBy(r => r).ToListAsync(Ct));
        Assert.Equal("1", (await db.Items.SingleAsync(i => i.ParentId == other && i.Kind == "run", Ct)).Ref);
    }

    [Fact]
    public async Task ScopeRef_ref_less_projects_number_within_their_own_workspace()
    {
        var (a, b) = (await WriteSeed.CreateAsync(fixture), await WriteSeed.CreateAsync(fixture));

        Assert.Equal(["project-1"], await CreatedRefsAsync(a, new NewItemArg("project", "Numbered one")));
        Assert.Equal(["project-2"], await CreatedRefsAsync(a, new NewItemArg("project", "Numbered two")));
        Assert.Equal(["project-1"], await CreatedRefsAsync(b, new NewItemArg("project", "Numbered elsewhere")));
    }

    [Fact]
    public async Task ScopeRef_a_duplicate_sibling_ref_is_refused_readably_and_another_workspace_may_hold_it()
    {
        var (a, b) = (await WriteSeed.CreateAsync(fixture), await WriteSeed.CreateAsync(fixture));

        foreach (var world in new[] { a, b })
        {
            await ProjectAsync(world, "dup");
            Assert.Equal(["x"], await CreatedRefsAsync(world, Run("dup", "the first x", "x")));
        }

        var run = await a.Writer.CreateAsync(a.WorkspaceId, WriteSeed.Orchestrator, [Run("dup", "a second x", "X")], Ct);
        var project = await a.Writer.CreateAsync(a.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "A second dup", Ref: "dup")], Ct);

        foreach (var (refused, holder) in new[] { (run, "dup already has a run \"X\""), (project, "this workspace already has a project \"dup\"") })
        {
            Assert.True(refused.Refused, refused.Text);
            RefusalShape.SelfCorrecting(refused.Text);
            Assert.Contains(holder, refused.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("schema refused", refused.Text, StringComparison.Ordinal);
        }

        // Under another parent the same run ref is a different handle.
        Assert.Equal(["x"], await CreatedRefsAsync(a, Run(await SeededProjectAsync(a), "x under the seeded project", "x")));

        await using var db = fixture.NewContext();
        foreach (var world in new[] { a, b })
        {
            Assert.Equal(1, await db.Items.CountAsync(i => i.WorkspaceId == world.WorkspaceId && i.Kind == "project" && i.Ref == "dup", Ct));
        }

        Assert.Equal(2, await db.Items.CountAsync(i => i.WorkspaceId == a.WorkspaceId && i.Kind == "run" && i.Ref == "x", Ct));
    }

    [Fact]
    public async Task ScopeRef_the_migration_renames_all_but_the_oldest_duplicate_then_holds_the_index()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }
            .ConnectionString;
        var options = WorkStateDbContextOptions.Build(connectionString);

        try
        {
            await using (var context = new WorkStateDbContext(options))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeScopeRefUnique, Ct);
            }

            var (gaia, oldest, younger, other) = (Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
            var workspaces = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };

            await using (var context = new WorkStateDbContext(options))
            {
                foreach (var workspace in workspaces)
                {
                    context.Workspaces.Add(new() { Id = workspace, Slug = $"w-{workspace:n}" });
                }

                var project = Seed.NewItem(workspaces[0], null, "project", "gaia", 1m);
                var first = Seed.NewItem(workspaces[0], project.Id, "run", "1", 1m);
                var second = Seed.NewItem(workspaces[0], project.Id, "run", "1", 2m);
                var elsewhere = Seed.NewItem(workspaces[1], null, "project", "gaia", 1m);
                var notDuplicate = Seed.NewItem(workspaces[1], elsewhere.Id, "run", "1", 1m);
                context.Items.AddRange(project, first, second, elsewhere, notDuplicate);
                await context.SaveChangesAsync(Ct);
                (gaia, oldest, younger, other) = (project.Id, first.Id, second.Id, notDuplicate.Id);
            }

            await using (var context = new WorkStateDbContext(options))
            {
                await context.Database.MigrateAsync(Ct);

                Assert.Equal("1", (await context.Items.SingleAsync(i => i.Id == oldest, Ct)).Ref);
                Assert.Equal("1-2", (await context.Items.SingleAsync(i => i.Id == younger, Ct)).Ref);
                Assert.Equal("1", (await context.Items.SingleAsync(i => i.Id == other, Ct)).Ref);

                var renamed = await context.Events.SingleAsync(e => e.ItemId == younger, Ct);
                Assert.Equal("updated", renamed.Kind);
                Assert.Contains("gaia/1 (run) renamed 1-2", renamed.Summary, StringComparison.Ordinal);
                Assert.Equal(1, await context.Events.CountAsync(Ct));
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*)::int FROM pg_indexes WHERE indexname = @index;", new { index = ScopeRefUnique.Index }));

                var duplicate = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    """
                    INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title)
                    VALUES (gen_random_uuid(), @workspace, @gaia, 'run', '1-2', 3, 'a third');
                    """,
                    new { workspace = workspaces[0], gaia }));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
                Assert.Equal(ScopeRefUnique.Index, duplicate.ConstraintName);
            }

            await using (var context = new WorkStateDbContext(options))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeScopeRefUnique, Ct);
                Assert.Equal("1-2", (await context.Items.SingleAsync(i => i.Id == younger, Ct)).Ref);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*)::int FROM pg_indexes WHERE indexname = @index;", new { index = ScopeRefUnique.Index }));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    private static NewItemArg Run(string parent, string title, string? reference = null)
        => new("run", title, Ref: reference, Parent: parent);

    private async Task<string> SeededProjectAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private async Task<Guid> ProjectAsync(WriteWorld world, string reference)
    {
        Assert.Equal([reference], await CreatedRefsAsync(world, new NewItemArg("project", $"Project {reference}", Ref: reference)));

        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Kind == "project" && i.Ref == reference, Ct)).Id;
    }

    private async Task<string[]> CreatedRefsAsync(WriteWorld world, params NewItemArg[] items)
    {
        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator, items, Ct);
        Assert.False(created.Refused, created.Text);

        var titles = items.Select(i => i.Title).ToArray();
        await using var db = fixture.NewContext();
        var rows = await db.Items
            .Where(i => i.WorkspaceId == world.WorkspaceId && titles.Contains(i.Title))
            .ToListAsync(Ct);
        return [.. titles.Select(t => rows.Single(r => r.Title == t).Ref)];
    }
}

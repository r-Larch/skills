using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// The live server applies ClaimPerTree at startup over whatever claims are held at that moment. A
/// database of its own, stopped one migration short, seeded with live claims in the old shape.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClaimMigrationTests(PostgresFixture fixture)
{
    private const string Before = "20260922101022_ConstraintRef";

    [Fact]
    public async Task Live_claims_keep_their_key_and_a_ref_fallback_key_still_binds_its_run()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }
            .ConnectionString;

        try
        {
            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(Before, Ct);
            }

            var (workspace, named, path) = await SeedOldShapeAsync(connectionString);

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);

                var claims = await context.Claims.AsNoTracking().ToListAsync(Ct);
                var byName = Assert.Single(claims, c => c.ItemId == named);
                var byPath = Assert.Single(claims, c => c.ItemId == path);

                Assert.Equal("run-a", byName.SerializationKey);
                Assert.Equal(workspace, byName.KeyWorkspaceId);
                Assert.Equal("P:/worktrees/shared", byPath.SerializationKey);
                Assert.Null(byPath.KeyWorkspaceId);
                Assert.All(claims, c => Assert.Null(c.Tree));
            }

            // The claim taken before the upgrade still holds its run's tree after it.
            await using var source = WorkStateDataSource.Create(connectionString);
            var writer = new WorkWriter(source, new BoardQueries(source), new Housekeeping(source));
            var refused = await writer.StatusAsync(
                workspace, "1.2", "in_progress", WriteSeed.Other, new StatusArgs(), Ct);

            Assert.True(refused.Refused, refused.Text);
            Assert.Contains("run-a/1.1", refused.Text, StringComparison.Ordinal);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>
    /// Raw SQL, because the EF model is already the new shape. One workspace: run-a with no tree of
    /// its own (its claim is keyed on the ref "run-a"), and run-b on a configured path.
    /// </summary>
    private static async Task<(Guid Workspace, Guid Named, Guid Path)> SeedOldShapeAsync(string connectionString)
    {
        var workspace = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var runA = Guid.CreateVersion7();
        var runB = Guid.CreateVersion7();
        var named = Guid.CreateVersion7();
        var sibling = Guid.CreateVersion7();
        var path = Guid.CreateVersion7();

        const string sql =
            """
            INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES
              (@project, @workspace, NULL,     'project', 'proj',  1, 'p'),
              (@runA,    @workspace, @project, 'run',     'run-a', 1, 'a'),
              (@runB,    @workspace, @project, 'run',     'run-b', 2, 'b');
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES
              (@named,   @workspace, @runA, 'task', '1.1', 1, 'held by name'),
              (@sibling, @workspace, @runA, 'task', '1.2', 2, 'its sibling'),
              (@path,    @workspace, @runB, 'task', '9.1', 1, 'held by path');
            INSERT INTO run_config (item_id, project_item_id, serialization_key) VALUES
              (@runA, @project, ''),
              (@runB, @project, 'P:/worktrees/shared');
            UPDATE item SET status = 'in_progress', started_at = now() WHERE id IN (@named, @path);
            INSERT INTO claim (id, item_id, serialization_key, agent_name, role, expires_at) VALUES
              (gen_random_uuid(), @named, 'run-a',               'orchestrator-a', 'orchestrator', now() + interval '30 minutes'),
              (gen_random_uuid(), @path,  'P:/worktrees/shared', 'orchestrator-a', 'orchestrator', now() + interval '30 minutes');
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(sql, new
        {
            workspace,
            slug = $"mig-{workspace:n}",
            project,
            runA,
            runB,
            named,
            sibling,
            path,
        });

        return (workspace, named, path);
    }
}

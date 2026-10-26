using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.AgentImport;

/// <summary>ImportGrant and ImportRecord, up over live rows, down to ProjectArchive, and up again.</summary>
[Collection(StoreCollection.Name)]
public sealed class ImportMigrationTests(PostgresFixture fixture)
{
    private const string BeforeImport = "20261026104621_ProjectArchive";

    [Fact]
    public async Task The_import_migrations_go_up_down_and_up_again()
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
                await context.Database.MigrateAsync(Ct);
            }

            var workspace = Guid.CreateVersion7();
            var project = Guid.CreateVersion7();
            var task = Guid.CreateVersion7();

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (@project, @workspace, 'project', 'p', 1, 'P');
                    INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES (@task, @workspace, @project, 'followup', 'f', 1, 'F');
                    INSERT INTO agent_session (id, workspace_id, name, role, grants)
                    VALUES (gen_random_uuid(), @workspace, 'aw-i', 'orchestrator', ARRAY['import']);
                    INSERT INTO import_record (id, workspace_id, project_item_id, source_path, kind, item_id, why, imported_by)
                    VALUES (gen_random_uuid(), @workspace, @project, 'TODO.md', 'followup', @task, 'still open', 'aw-i');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}", project, task });

                var unknownGrant = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    "INSERT INTO agent_session (id, workspace_id, name, role, grants) VALUES (gen_random_uuid(), @workspace, 'aw-x', 'worker', ARRAY['admin']);",
                    new { workspace }));
                Assert.Equal(PostgresErrorCodes.CheckViolation, unknownGrant.SqlState);

                var twoTargets = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    """
                    INSERT INTO import_record (id, workspace_id, project_item_id, source_path, source_anchor, kind, item_id, note_id, why, imported_by)
                    SELECT gen_random_uuid(), @workspace, @project, 'TODO.md', 'L9', 'followup', @task, n.id, 'x', 'aw-i'
                    FROM (SELECT gen_random_uuid() AS id) n;
                    """,
                    new { workspace, project, task }));
                Assert.True(twoTargets.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation);

                var duplicate = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    """
                    INSERT INTO import_record (id, workspace_id, project_item_id, source_path, kind, item_id, why, imported_by)
                    VALUES (gen_random_uuid(), @workspace, @project, 'TODO.md', 'followup', @task, 'again', 'aw-i');
                    """,
                    new { workspace, project, task }));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeImport, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Empty(await connection.QueryAsync<string>(
                    "SELECT column_name FROM information_schema.columns WHERE table_name = 'agent_session' AND column_name = 'grants';"));
                Assert.False(await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'import_record');"));
                Assert.Equal("aw-i", await connection.ExecuteScalarAsync<string>(
                    "SELECT name FROM agent_session WHERE workspace_id = @workspace;", new { workspace }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
                Assert.Empty((await context.AgentSessions.SingleAsync(s => s.WorkspaceId == workspace, Ct)).Grants);
                Assert.Empty(await context.ImportRecords.ToListAsync(Ct));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }
}

using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>SessionClientKey, up over live rows, down to ImportRecord, and up again.</summary>
[Collection(StoreCollection.Name)]
public sealed class SessionClientKeyMigrationTests(PostgresFixture fixture)
{
    private const string BeforeClientKey = "20261026112426_ImportRecord";

    [Fact]
    public async Task The_client_key_migration_goes_up_down_and_up_again()
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

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO agent_session (id, workspace_id, name, role, client_key)
                    VALUES (gen_random_uuid(), @workspace, 'skills-5d', 'controller', 'host/1/2');
                    INSERT INTO agent_session (id, workspace_id, name, role) VALUES (gen_random_uuid(), @workspace, 'a', 'worker');
                    INSERT INTO agent_session (id, workspace_id, name, role) VALUES (gen_random_uuid(), @workspace, 'b', 'worker');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}" });

                var twice = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    "INSERT INTO agent_session (id, workspace_id, name, role, client_key) VALUES (gen_random_uuid(), @workspace, 'c', 'worker', 'host/1/2');",
                    new { workspace }));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, twice.SqlState);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeClientKey, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Empty(await connection.QueryAsync<string>(
                    "SELECT column_name FROM information_schema.columns WHERE table_name = 'agent_session' AND column_name = 'client_key';"));
                Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM agent_session WHERE workspace_id = @workspace;", new { workspace }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
                Assert.All(
                    await context.AgentSessions.Where(s => s.WorkspaceId == workspace).ToListAsync(Ct),
                    s => Assert.Null(s.ClientKey));
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

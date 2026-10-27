using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>RefLookupIndex, up over live rows, down to SessionClientKey, and up again.</summary>
[Collection(StoreCollection.Name)]
public sealed class RefLookupIndexMigrationTests(PostgresFixture fixture)
{
    private const string BeforeRefLookup = "20261026154034_SessionClientKey";

    private const string IndexExists = "SELECT count(*) FROM pg_indexes WHERE indexname = 'item_ref_lower_idx';";

    [Fact]
    public async Task The_ref_lookup_index_goes_up_down_and_up_again()
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
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title)
                    VALUES (gen_random_uuid(), @workspace, 'project', 'Alpha', 1, 'a'),
                           (gen_random_uuid(), @workspace, 'project', 'beta', 2, 'b');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}" });

                Assert.Equal(1, await connection.ExecuteScalarAsync<int>(IndexExists));

                await connection.ExecuteAsync("SET enable_seqscan = off;");
                var plan = string.Join("\n", await connection.QueryAsync<string>(
                    "EXPLAIN SELECT id FROM item WHERE workspace_id = @workspace AND lower(ref) = lower('ALPHA');",
                    new { workspace }));
                Assert.Contains("item_ref_lower_idx", plan);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeRefLookup, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>(IndexExists));
                Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM item WHERE workspace_id = @workspace;", new { workspace }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>(IndexExists));
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

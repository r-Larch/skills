using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>NoteSearchIndex, up over live rows, down to RefLookupIndex, and up again.</summary>
[Collection(StoreCollection.Name)]
public sealed class NoteSearchIndexMigrationTests(PostgresFixture fixture)
{
    private const string BeforeNoteSearch = "20261027003240_RefLookupIndex";

    private const string IndexExists = "SELECT count(*) FROM pg_indexes WHERE indexname = 'note_search_idx';";

    [Fact]
    public async Task The_note_search_index_goes_up_down_and_up_again_and_search_uses_it()
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
                    INSERT INTO note (id, workspace_id, kind, body)
                    VALUES (gen_random_uuid(), @workspace, 'observation', 'the wombat index is warm'),
                           (gen_random_uuid(), @workspace, 'decision', 'nothing to see');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}" });

                Assert.Equal(1, await connection.ExecuteScalarAsync<int>(IndexExists));

                await connection.ExecuteAsync("SET enable_seqscan = off;");
                var plan = string.Join("\n", await connection.QueryAsync<string>(
                    "EXPLAIN SELECT id FROM note WHERE to_tsvector('english', body) @@ websearch_to_tsquery('english', 'wombat');"));
                Assert.Contains("note_search_idx", plan);
            }

            await using (var source = WorkStateDataSource.Create(connectionString))
            {
                var hit = Assert.Single(await new WorkStateQueries(source).SearchAsync(workspace, "wombat", cancellationToken: Ct));
                Assert.Equal("note", hit.Source);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeNoteSearch, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>(IndexExists));
                Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM note WHERE workspace_id = @workspace;", new { workspace }));
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

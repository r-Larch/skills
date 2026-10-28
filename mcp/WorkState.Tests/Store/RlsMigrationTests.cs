using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Migrations;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>TenantRls, up over live rows, down to SecurityStamp, and up again.</summary>
[Collection(StoreCollection.Name)]
public sealed class RlsMigrationTests(PostgresFixture fixture)
{
    private const string BeforeRls = "20261028195513_SecurityStamp";

    [Fact]
    public async Task Rls_migration_goes_up_down_and_up_again()
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
            await MigrateAsync(connectionString, null);

            var workspace = Guid.CreateVersion7();
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title)
                    VALUES (gen_random_uuid(), @workspace, 'project', 'Alpha', 1, 'a');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}" });

                await AssertUpAsync(connection);
            }

            await MigrateAsync(connectionString, BeforeRls);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                var down = await StateAsync(connection);
                Assert.Equal(new State(0, 0, 0, false, 0), down);
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM item;"));
            }

            await MigrateAsync(connectionString, null);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await AssertUpAsync(connection);
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM item;"));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        await using var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString));
        await context.GetService<IMigrator>().MigrateAsync(target, Ct);
    }

    private static async Task AssertUpAsync(NpgsqlConnection connection)
    {
        var tables = TenantRls.Direct.Count + TenantRls.ThroughParent.Count;
        Assert.Equal(new State(tables, tables, TenantRls.Views.Count, true, 2), await StateAsync(connection));
    }

    private static Task<State> StateAsync(NpgsqlConnection connection)
        => connection.QuerySingleAsync<State>(
            $"""
             SELECT (SELECT count(*) FROM pg_policy WHERE polname = '{TenantRls.Policy}')                AS "Policies",
                    (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND c.relrowsecurity AND c.relforcerowsecurity)        AS "Forced",
                    (SELECT count(*) FROM pg_class c
                      WHERE c.relkind = 'v' AND 'security_invoker=true' = ANY (c.reloptions))           AS "InvokerViews",
                    CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{WorkStateConnections.AppRole}')
                         THEN has_table_privilege('{WorkStateConnections.AppRole}', 'item', 'SELECT')
                         ELSE false END                                                                 AS "Granted",
                    (SELECT count(*) FROM pg_default_acl d JOIN pg_namespace n ON n.oid = d.defaclnamespace
                      WHERE n.nspname = 'public'
                        AND d.defaclacl::text LIKE '%{WorkStateConnections.AppRole}=%')                 AS "DefaultGrants";
             """);

    private sealed record State(long Policies, long Forced, long InvokerViews, bool Granted, long DefaultGrants);
}

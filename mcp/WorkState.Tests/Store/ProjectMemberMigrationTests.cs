using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>ProjectMembers over live rows: every project that exists gets every member of its workspace, and no other.</summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectMemberMigrationTests(PostgresFixture fixture)
{
    private const string BeforeMembers = "20261028225916_ScopeRefUnique";

    [Fact]
    public async Task ProjectMember_the_migration_seeds_every_tenant_member_into_every_project_and_goes_down_and_up()
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
            await MigrateAsync(connectionString, BeforeMembers);

            // Two workspaces: the first with two users, two projects and a run; the second with one user, one
            // project; and a third with a project and nobody in it.
            var (one, two, empty) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (alpha, beta, gamma, lonely, run) =
                (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var (ann, bob, cat) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@one, 'one'), (@two, 'two'), (@empty, 'empty');
                    INSERT INTO app_user (id, name, password_hash) VALUES (@ann, 'ann', 'x'), (@bob, 'bob', 'x'), (@cat, 'cat', 'x');
                    INSERT INTO tenant_membership (user_id, workspace_id, role)
                    VALUES (@ann, @one, 'admin'), (@bob, @one, 'member'), (@cat, @two, 'admin');
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title)
                    VALUES (@alpha, @one, 'project', 'alpha', 1, 'a'), (@beta, @one, 'project', 'beta', 2, 'b'),
                           (@gamma, @two, 'project', 'gamma', 1, 'g'), (@lonely, @empty, 'project', 'lonely', 1, 'l');
                    INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title)
                    VALUES (@run, @one, @alpha, 'run', 'r', 1, 'r');
                    """,
                    new { one, two, empty, alpha, beta, gamma, lonely, run, ann, bob, cat });
            }

            await MigrateAsync(connectionString, null);

            (Guid Project, Guid User)[] expected =
                [(alpha, ann), (alpha, bob), (beta, ann), (beta, bob), (gamma, cat)];
            Assert.Equal(expected.Order(), (await RowsAsync(connectionString)).Order());

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM project_member WHERE added_by IS NOT NULL;"));
                Assert.Equal(5, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM project_member pm JOIN item i ON i.id = pm.project_item_id WHERE pm.workspace_id = i.workspace_id;"));
            }

            await MigrateAsync(connectionString, BeforeMembers);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('project_member')::text;"));
            }

            await MigrateAsync(connectionString, null);
            Assert.Equal(expected.Order(), (await RowsAsync(connectionString)).Order());
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

    private static async Task<List<(Guid Project, Guid User)>> RowsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return (await connection.QueryAsync<(Guid, Guid)>("SELECT project_item_id, user_id FROM project_member;")).AsList();
    }
}

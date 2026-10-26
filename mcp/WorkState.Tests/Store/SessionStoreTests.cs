using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// doc 11's tree rule as constraints: one session may hold several items of a tree, a second
/// session may not, and a claim with no session still holds its tree alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SessionStoreTests(PostgresFixture fixture)
{
    private const string BeforeSessions = "20261022160602_ClaimPerTree";

    [Fact]
    public async Task One_session_holds_two_items_of_a_tree_and_a_second_session_is_excluded()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var key = $"P:/worktrees/{Guid.CreateVersion7():n}";
        var (holder, other) = (await SessionAsync(world.WorkspaceId), await SessionAsync(world.WorkspaceId));

        var items = new[] { "ss1", "ss2", "ss3" }
            .Select((r, n) => Seed.NewItem(world.WorkspaceId, world.RunA, "task", r, n + 1m))
            .ToArray();

        await using (var context = fixture.NewContext())
        {
            context.Items.AddRange(items);
            context.Claims.Add(NewClaim(items[0].Id, key, holder));
            context.Claims.Add(NewClaim(items[1].Id, key, holder));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Claims.Add(NewClaim(items[2].Id, key, other));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("23P01", error.SqlState);
        Assert.Equal("claim_one_session_per_tree", error.ConstraintName);
    }

    [Fact]
    public async Task A_claim_with_no_session_excludes_a_session_from_its_tree()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var key = $"P:/worktrees/{Guid.CreateVersion7():n}";
        var session = await SessionAsync(world.WorkspaceId);

        var first = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "sn1", 1m);
        var second = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "sn2", 2m);

        await using (var context = fixture.NewContext())
        {
            context.Items.AddRange(first, second);
            context.Claims.Add(NewClaim(first.Id, key, null));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Claims.Add(NewClaim(second.Id, key, session));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("claim_one_session_per_tree", error.ConstraintName);
    }

    /// <summary>
    /// The Down a rollback would run, on a database of its own: a session holding two items of one
    /// tree is the one state the restored index cannot hold, so Down must release one, and Up must
    /// apply again over what Down left.
    /// </summary>
    [Fact]
    public async Task The_sessions_migration_goes_down_and_up_again_over_live_claims()
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

            var (first, second) = await SeedTwoClaimsOfOneSessionAsync(connectionString);

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeSessions, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                var live = (await connection.QueryAsync<Guid>(
                    "SELECT item_id FROM claim WHERE released_at IS NULL;")).ToList();
                Assert.Equal([first], live);

                Assert.False(await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'agent_session');"));

                var index = await connection.ExecuteScalarAsync<string>(
                    "SELECT indexdef FROM pg_indexes WHERE indexname = 'claim_one_writer_per_tree';");
                Assert.DoesNotContain("session_id", index, StringComparison.Ordinal);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
                Assert.Null((await context.Claims.SingleAsync(c => c.ItemId == second, Ct)).SessionId);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    private static async Task<(Guid First, Guid Second)> SeedTwoClaimsOfOneSessionAsync(string connectionString)
    {
        var workspace = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var run = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();

        const string sql =
            """
            INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES
              (@project, @workspace, NULL,     'project', 'proj',  1, 'p'),
              (@run,     @workspace, @project, 'run',     'run-a', 1, 'a');
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES
              (@first,  @workspace, @run, 'task', '1.1', 1, 'first'),
              (@second, @workspace, @run, 'task', '1.2', 2, 'second');
            INSERT INTO agent_session (id, workspace_id, name, role) VALUES (@session, @workspace, 'aw-m4', 'controller');
            INSERT INTO claim (id, item_id, serialization_key, agent_name, role, expires_at, acquired_at, session_id) VALUES
              (gen_random_uuid(), @first,  'P:/worktrees/m4', 'controller', 'controller', now() + interval '30 minutes', now() - interval '1 minute', @session),
              (gen_random_uuid(), @second, 'P:/worktrees/m4', 'controller', 'controller', now() + interval '30 minutes', now(), @session);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(sql, new
        {
            workspace,
            slug = $"mig-{workspace:n}",
            project,
            run,
            first,
            second,
            session,
        });

        return (first, second);
    }

    private async Task<Guid> SessionAsync(Guid workspaceId)
    {
        await using var context = fixture.NewContext();
        var session = new AgentSession
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            Name = $"s-{Guid.CreateVersion7():n}",
            Role = ActorRole.Controller,
        };

        context.AgentSessions.Add(session);
        await context.SaveChangesAsync(Ct);
        return session.Id;
    }

    private static Claim NewClaim(Guid itemId, string key, Guid? session) => new()
    {
        Id = Guid.CreateVersion7(),
        ItemId = itemId,
        SerializationKey = key,
        AgentName = "controller",
        Role = ActorRole.Controller,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        SessionId = session,
    };
}

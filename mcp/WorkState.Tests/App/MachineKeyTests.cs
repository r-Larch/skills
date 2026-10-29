using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ModelContextProtocol.Client;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m60. A path names a folder on one person's machine: a <c>root</c> binding and a path tree lock are per user, so
/// two users' same path are two checkouts. A remote names one repository for everyone and a name lock one branch of
/// the workspace, so both stay the tenant's. Project membership still decides what a binding may reveal.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class MachineKeyTests(PostgresFixture fixture)
{
    private const string Root = "P:/Projects/Shared";
    private const string RootKey = "p:/projects/shared";
    private const string Remote = "https://github.com/acme/shared.git";
    private const string Before = "20261029090100_ProjectAccessSeq";

    [Fact]
    public async Task MachineKey_two_users_bind_the_same_root_to_different_projects_and_each_finds_only_their_own()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await using var u1 = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);
        await using var u2 = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);

        await OkAsync(u1, "work_update", Args(("ref", ProjectWorld.P1), ("repo", new { root = Root })));
        await OkAsync(u2, "work_update", Args(("ref", ProjectWorld.P2), ("repo", new { root = @"p:\projects\shared\" })));

        Assert.Equal(
            [$"{world.P1Id} {world.User1Id}", $"{world.P2Id} {world.User2Id}"],
            (await RowsAsync(world.WorkspaceId, RootKey)).Order());

        var mine = await OkAsync(u1, "work_brief", Args(("repo", new { root = Root })));
        Assert.Contains($"PROJECT {ProjectWorld.P1} ", mine, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectWorld.P2, mine, StringComparison.Ordinal);

        var theirs = await OkAsync(u2, "work_brief", Args(("repo", new { root = Root })));
        Assert.Contains($"PROJECT {ProjectWorld.P2} ", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain($"PROJECT {ProjectWorld.P1} ", theirs, StringComparison.Ordinal);

        // A member of both sees P1, but not the root User1 bound on their own machine.
        Assert.Contains(RootKey, await OkAsync(u1, "work_get", Args(("refs", new[] { ProjectWorld.P1 }))), StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, await OkAsync(u2, "work_get", Args(("refs", new[] { ProjectWorld.P1 }))), StringComparison.Ordinal);
        Assert.Contains(RootKey, await OkAsync(u2, "work_get", Args(("refs", new[] { ProjectWorld.P2 }))), StringComparison.Ordinal);

        // A credential with no user holds NULL rows only, and none is bound here.
        await using var anonymous = await world.McpAsync(world.Static.Mcp!);
        var unbound = await OkAsync(anonymous, "work_brief", Args(("repo", new { root = Root })));
        Assert.DoesNotContain($"PROJECT {ProjectWorld.P1} ", unbound, StringComparison.Ordinal);
        Assert.DoesNotContain($"PROJECT {ProjectWorld.P2} ", unbound, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MachineKey_a_remote_binds_once_per_tenant_resolves_for_every_member_and_for_no_non_member()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await using var u1 = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);
        await using var u2 = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);
        await using var anonymous = await world.McpAsync(world.Static.Mcp!);

        await OkAsync(u2, "work_update", Args(("ref", ProjectWorld.P1), ("repo", new { remote = Remote })));
        Assert.Equal([$"{world.P1Id} "], await RowsAsync(world.WorkspaceId, "github.com/acme/shared"));

        foreach (var member in new[] { u1, u2, anonymous })
        {
            Assert.Contains($"PROJECT {ProjectWorld.P1} ", await OkAsync(member, "work_brief", Args(("repo", new { remote = Remote }))), StringComparison.Ordinal);
        }

        await OkAsync(u1, "work_update", Args(("ref", ProjectWorld.P1), ("repo", new { remote = Remote })));

        var twice = await ReadServer.CallEitherAsync(u2, "work_update", Args(("ref", ProjectWorld.P2), ("repo", new { remote = Remote })), Ct);
        Assert.True(twice.Refused, twice.Text);
        Assert.Contains($"already bound to project {ProjectWorld.P1}", twice.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(twice.Text);

        // User1 is no member of P2: its remote finds nothing, and binding it names no project.
        var hidden = await OkAsync(u1, "work_brief", Args(("repo", new { remote = ProjectWorld.RemoteP2 })));
        Assert.DoesNotContain($"PROJECT {ProjectWorld.P2} ", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectWorld.TitleP2, hidden, StringComparison.Ordinal);

        var taken = await ReadServer.CallEitherAsync(u1, "work_update", Args(("ref", ProjectWorld.P1), ("repo", new { remote = ProjectWorld.RemoteP2 })), Ct);
        Assert.True(taken.Refused, taken.Text);
        Assert.Contains("not a member of", taken.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"project {ProjectWorld.P2}", taken.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MachineKey_a_path_lock_is_per_user_and_a_name_lock_is_per_workspace()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        foreach (var reference in new[] { "1.2", "1.3", "1.4", "1.5" })
        {
            await WriteSeed.ChildAsync(world.One, world.One.PhaseId, reference);
        }

        var user1 = await world.CredentialsAsync(world.User1);
        await using var u1 = await world.McpAsync(user1[1].Mcp!);
        await using var u1Session = await world.McpAsync(user1[2].Mcp!);
        await using var u2 = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);

        await OkAsync(u1, "work_status", Start("1.1", "P:/x"));

        // The same folder spelled otherwise, on User2's machine: another checkout.
        await OkAsync(u2, "work_status", Start("1.2", @"p:\x\"));

        var sameUser = await ReadServer.CallEitherAsync(u1Session, "work_status", Start("1.3", "P:/x"), Ct);
        Assert.True(sameUser.Refused, sameUser.Text);
        Assert.Contains("Another writer holds this working tree", sameUser.Text, StringComparison.Ordinal);
        Assert.Contains("run-a/1.1", sameUser.Text, StringComparison.Ordinal);

        await OkAsync(u1, "work_status", Start("1.4", "feature/m60"));
        var branch = await ReadServer.CallEitherAsync(u2, "work_status", Start("1.5", "refs/heads/feature/m60"), Ct);
        Assert.True(branch.Refused, branch.Text);
        Assert.Contains("Another writer holds this working tree", branch.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(branch.Text);

        var keys = await ClaimKeysAsync(world);
        Assert.Equal(world.User1Id, keys["1.1"]);
        Assert.Equal(world.User2Id, keys["1.2"]);
        Assert.Null(keys["1.4"]);
        Assert.False(keys.ContainsKey("1.3") || keys.ContainsKey("1.5"));

        // A non-member claims nothing of P2, on a path or otherwise.
        var hidden = await ReadServer.CallEitherAsync(u1, "work_status", Args(("ref", ProjectWorld.TaskP2Path), ("to", "in_progress"), ("tree", "P:/y")), Ct);
        Assert.True(hidden.Refused, hidden.Text);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync(world.P2TaskId));
    }

    [Fact]
    public async Task MachineKey_the_migration_copies_each_root_to_who_can_see_its_project_and_keeps_remotes_tenant_wide()
    {
        var database = $"mig_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        var (workspace, p, q, u1, u2, u3, session) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        try
        {
            await MigrateAsync(connectionString, Before);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO app_user (id, name, password_hash) VALUES (@u1, @n1, 'x'), (@u2, @n2, 'x'), (@u3, @n3, 'x');
                    INSERT INTO tenant_membership (user_id, workspace_id, role)
                    VALUES (@u1, @workspace, 'member'), (@u2, @workspace, 'member'), (@u3, @workspace, 'member');
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title)
                    VALUES (@p, @workspace, 'project', 'p', 1, 'P'), (@q, @workspace, 'project', 'q', 2, 'Q');
                    INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@p, @u1, @workspace), (@p, @u2, @workspace);
                    INSERT INTO project_repo (id, workspace_id, project_item_id, kind, key)
                    VALUES (gen_random_uuid(), @workspace, @p, 'root', 'p:/x'),
                           (gen_random_uuid(), @workspace, @p, 'remote', 'github.com/a/b'),
                           (gen_random_uuid(), @workspace, @q, 'root', 'p:/q');
                    INSERT INTO agent_session (id, workspace_id, name, role, user_id) VALUES (@session, @workspace, 'mk-s', 'orchestrator', @u1);
                    INSERT INTO claim (id, item_id, serialization_key, key_workspace_id, writes, agent_name, role, expires_at, session_id)
                    VALUES (gen_random_uuid(), @p, 'p:/x', @workspace, true, 'mk', 'orchestrator', now() + interval '1 hour', @session),
                           (gen_random_uuid(), @q, 'tree-x', @workspace, true, 'mk', 'orchestrator', now() + interval '1 hour', @session);
                    """,
                    new { workspace, slug = $"mk-{workspace:n}", p, q, u1, u2, u3, session, n1 = $"mk1-{u1:n}", n2 = $"mk2-{u2:n}", n3 = $"mk3-{u3:n}" });
            }

            await MigrateAsync(connectionString, null);
            var after = await BindingsAsync(connectionString);
            Assert.Equal(Expected(p, "root p:/x", null, u1, u2), after.Where(r => r.StartsWith($"{p} root", StringComparison.Ordinal)).Order());
            Assert.Equal([$"{p} remote github.com/a/b "], after.Where(r => r.Contains("remote", StringComparison.Ordinal)));
            Assert.Equal(Expected(q, "root p:/q", null, u1, u2, u3), after.Where(r => r.StartsWith($"{q}", StringComparison.Ordinal)).Order());

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                var claims = (await connection.QueryAsync<(string Key, Guid? User)>(
                    "SELECT serialization_key, key_user_id FROM claim;")).ToDictionary(c => c.Key, c => c.User);
                Assert.Equal(u1, claims["p:/x"]);
                Assert.Null(claims["tree-x"]);

                var duplicate = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    "INSERT INTO project_repo (id, workspace_id, project_item_id, user_id, kind, key) VALUES (gen_random_uuid(), @workspace, @q, @u1, 'root', 'p:/x');",
                    new { workspace, q, u1 }));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            }

            await MigrateAsync(connectionString, Before);
            Assert.Equal(
                new[] { $"{p} remote github.com/a/b", $"{p} root p:/x", $"{q} root p:/q" }.Order(),
                (await KeysBeforeAsync(connectionString)).Order());

            await MigrateAsync(connectionString, null);
            Assert.Equal(after.Order(), (await BindingsAsync(connectionString)).Order());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    private static IEnumerable<string> Expected(Guid project, string key, params Guid?[] users)
        => users.Select(u => $"{project} {key} {u}").Order();

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        await using var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString));
        if (target is null)
        {
            await context.Database.MigrateAsync(Ct);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        }
        else
        {
            await context.GetService<IMigrator>().MigrateAsync(target, Ct);
        }
    }

    private static async Task<List<string>> BindingsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return [.. await connection.QueryAsync<string>(
            "SELECT project_item_id || ' ' || kind || ' ' || key || ' ' || coalesce(user_id::text, '') FROM project_repo;")];
    }

    private static async Task<List<string>> KeysBeforeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        Assert.Null(await connection.ExecuteScalarAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'project_repo' AND column_name = 'user_id';"));
        return [.. await connection.QueryAsync<string>("SELECT project_item_id || ' ' || kind || ' ' || key FROM project_repo;")];
    }

    /// <summary>Each binding of <paramref name="key"/> as "project user", the user empty for none.</summary>
    private async Task<List<string>> RowsAsync(Guid workspace, string key)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return [.. await connection.QueryAsync<string>(
            "SELECT project_item_id || ' ' || coalesce(user_id::text, '') FROM project_repo WHERE workspace_id = @workspace AND key = @key;",
            new { workspace, key })];
    }

    /// <summary>Each live claim of P1's tasks, by ref, and the user its key is held for.</summary>
    private async Task<Dictionary<string, Guid?>> ClaimKeysAsync(ProjectWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<(string Ref, Guid? User)>(
                """
                SELECT i.ref, c.key_user_id FROM claim c JOIN item i ON i.id = c.item_id
                WHERE  c.released_at IS NULL AND i.ancestors @> ARRAY[@p1]::uuid[];
                """,
                new { p1 = world.P1Id }))
            .ToDictionary(c => c.Ref, c => c.User);
    }

    private static IReadOnlyDictionary<string, object?> Start(string task, string tree)
        => Args(("ref", $"{ProjectWorld.P1}/run-a/{task}"), ("to", "in_progress"), ("tree", tree));

    private static async Task<string> OkAsync(McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
    {
        var (text, refused) = await ReadServer.CallEitherAsync(mcp, tool, args, Ct);
        Assert.False(refused, $"{tool} was refused:\n{text}");
        return text;
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);
}

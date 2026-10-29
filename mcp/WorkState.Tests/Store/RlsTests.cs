using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Migrations;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// doc 02 §15: row-level security per workspace. Every query here is deliberately unfiltered — the database alone
/// has to keep the other tenant out. Each test opens its own data source on the fixture's database: the fixture's
/// is marked to run unscoped callers as the owner, these are not.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RlsTests(PostgresFixture fixture) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>The API creates its first user only while <c>app_user</c> is empty, so the seed's users go again.</summary>
    public async ValueTask DisposeAsync()
    {
        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        await owner.ExecuteAsync($"DELETE FROM app_user WHERE name LIKE '{RlsWorld.UserPrefix}%';");
    }

    [Fact]
    public async Task Rls_an_unfiltered_query_through_the_tenant_helper_returns_only_the_tenants_rows()
    {
        var a = await RlsWorld.CreateAsync(fixture);
        var b = await RlsWorld.CreateAsync(fixture);
        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        var connections = new WorkStateConnections(source);

        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        using (TenantScope.Enter(a.WorkspaceId))
        {
            foreach (var (table, predicate) in Tables())
            {
                await using var tenant = await connections.TenantAsync(Ct);
                var seen = await tenant.Connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table};");
                var theirs = await owner.ExecuteScalarAsync<long>(
                    $"SELECT count(*) FROM {table} WHERE {predicate};", new { tenant = a.WorkspaceId });
                var all = await owner.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table};");

                Assert.True(theirs > 0, $"{table}: the seed left tenant A without a row");
                Assert.True(seen == theirs, $"{table}: saw {seen} rows, tenant A has {theirs}");
                Assert.True(all > seen, $"{table}: tenant B's rows are not in the table, so this proves nothing");
            }

            await using (var tenant = await connections.TenantAsync(Ct))
            {
                // The views run as their caller: B's subtree and B's threads are not there to aggregate.
                Assert.Equal(0, await tenant.Connection.ExecuteScalarAsync<long>(
                    "SELECT count(*) FROM item_rollup WHERE id = @project;", new { project = b.ProjectId }));
                Assert.Equal(0, await tenant.Connection.ExecuteScalarAsync<long>(
                    "SELECT count(*) FROM open_thread WHERE workspace_id IS DISTINCT FROM @a;", new { a = a.WorkspaceId }));
                Assert.Equal(1, await tenant.Connection.ExecuteScalarAsync<long>(
                    "SELECT count(*) FROM item_rollup WHERE id = @project;", new { project = a.ProjectId }));
            }
        }

        Assert.Equal(1, await owner.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_rollup WHERE id = @project;", new { project = b.ProjectId }));
    }

    [Fact]
    public async Task Rls_a_write_into_another_workspace_is_refused_and_its_rows_cannot_be_touched()
    {
        var a = await RlsWorld.CreateAsync(fixture);
        var b = await RlsWorld.CreateAsync(fixture);
        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        var connections = new WorkStateConnections(source);

        using var scope = TenantScope.Enter(a.WorkspaceId);

        string[] refused =
        [
            "INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (gen_random_uuid(), @b, 'project', 'rls-x', 9, 'x');",
            "UPDATE item SET workspace_id = @b WHERE id = @aProject;",
            "INSERT INTO item_criterion (id, item_id, seq, text, checked) VALUES (gen_random_uuid(), @bRun, 9, 'x', false);",
            "INSERT INTO note (id, workspace_id, kind, body) VALUES (gen_random_uuid(), @b, 'observation', 'x');",
            "INSERT INTO event (workspace_id, actor, role, kind) VALUES (@b, 'rls', 'system', 'rls_probe');",
        ];

        var args = new { b = b.WorkspaceId, aProject = a.ProjectId, bRun = b.RunId };
        foreach (var statement in refused)
        {
            await using var tenant = await connections.TenantAsync(Ct);
            var error = await Seed.RefusedAsync(() => tenant.Connection.ExecuteAsync(statement, args));
            Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{statement} → {error.SqlState} {error.MessageText}");
        }

        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.Equal(0, await tenant.Connection.ExecuteAsync(
                "UPDATE item SET title = 'taken' WHERE workspace_id = @b;", args));
            Assert.Equal(0, await tenant.Connection.ExecuteAsync(
                "DELETE FROM item_criterion WHERE item_id = @bRun;", args));
            await tenant.CommitAsync(Ct);
        }

        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        Assert.Equal(0, await owner.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item WHERE workspace_id = @b AND title = 'taken';", args));
        Assert.Equal(1, await owner.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item_criterion WHERE item_id = @bRun;", args));
    }

    /// <summary>
    /// One physical connection, three callers. Reset on close is switched off, so what keeps A's setting from B
    /// is that both settings are transaction-local, not that Npgsql happened to send DISCARD ALL.
    /// </summary>
    [Fact]
    public async Task Rls_a_pooled_connection_reused_by_tenant_B_never_carries_tenant_As_setting()
    {
        var a = await RlsWorld.CreateAsync(fixture);
        var b = await RlsWorld.CreateAsync(fixture);
        var single = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1, NoResetOnClose = true };
        await using var source = WorkStateDataSource.Create(single.ConnectionString);
        var connections = new WorkStateConnections(source);

        int backend;
        using (TenantScope.Enter(a.WorkspaceId))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            var seen = await SettingsAsync(tenant.Connection);
            Assert.Equal((WorkStateConnections.AppRole, a.WorkspaceId.ToString()), (seen.User, seen.Workspace));
            backend = seen.Backend;
        }

        using (TenantScope.Enter(b.WorkspaceId))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            var seen = await SettingsAsync(tenant.Connection);
            Assert.Equal(backend, seen.Backend);
            Assert.Equal((WorkStateConnections.AppRole, b.WorkspaceId.ToString()), (seen.User, seen.Workspace));
            Assert.Equal(0, await tenant.Connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM item WHERE workspace_id = @a;", new { a = a.WorkspaceId }));
        }

        // A tenant transaction that failed half-way leaves nothing behind either.
        using (TenantScope.Enter(a.WorkspaceId))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            await Seed.RefusedAsync(() => tenant.Connection.ExecuteAsync("SELECT 1 / 0;"));
        }

        await using (var bypass = await connections.OwnerBypassAsync(Bypass.Authentication, Ct))
        {
            var seen = await SettingsAsync(bypass);
            Assert.Equal(backend, seen.Backend);
            Assert.Equal(single.Username, seen.User);
            Assert.True(string.IsNullOrEmpty(seen.Workspace), $"the owner's connection still carries {seen.Workspace}");
            Assert.True(await bypass.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM item WHERE workspace_id IN (@a, @b);", new { a = a.WorkspaceId, b = b.WorkspaceId }) >= 6);
        }
    }

    [Fact]
    public async Task Rls_with_no_tenant_set_the_tenant_helper_sees_no_rows_not_all_rows()
    {
        var a = await RlsWorld.CreateAsync(fixture);
        var single = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1 };
        await using var source = WorkStateDataSource.Create(single.ConnectionString);
        var connections = new WorkStateConnections(source);

        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            Assert.True(await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM item;") > 0);
        }

        // Outside every scope; then a scope whose principal has no tenant, on the connection tenant A just
        // used — its setting reset to '' rather than unset, which the policy reads as no tenant.
        await AssertSeesNothingAsync();

        using (TenantScope.Enter(a.WorkspaceId))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.True(await tenant.Connection.ExecuteScalarAsync<long>("SELECT count(*) FROM item;") > 0);
        }

        using (TenantScope.Enter(null))
        {
            await AssertSeesNothingAsync();
        }

        async Task AssertSeesNothingAsync()
        {
            foreach (var (table, _) in Tables())
            {
                await using var tenant = await connections.TenantAsync(Ct);
                Assert.True(
                    await tenant.Connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table};") == 0,
                    $"{table} shows rows to a connection with no tenant");
            }

            await using (var tenant = await connections.TenantAsync(Ct))
            {
                var error = await Seed.RefusedAsync(() => tenant.Connection.ExecuteAsync(
                    "INSERT INTO note (id, workspace_id, kind, body) VALUES (gen_random_uuid(), @a, 'observation', 'x');",
                    new { a = a.WorkspaceId }));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
    }

    /// <summary>
    /// doc 15 §7: under a policy, a function that is not LEAKPROOF cannot be an index condition, and the subtree,
    /// ref and search reads fell back to scanning the workspace. Each predicate must be the index's own condition,
    /// not a filter over an index scan by workspace. A database of its own, with one workspace: the tenant is an
    /// InitPlan, so the planner sizes it as an average workspace, which in the shared database is a few rows.
    /// </summary>
    [Fact]
    public async Task Rls_the_subtree_ref_and_search_indexes_still_serve_reads_under_the_tenant_role()
    {
        var database = $"rls_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }
            .ConnectionString;
        var (ws, project, run) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        (string Sql, string Index, string Condition)[] reads =
        [
            ("SELECT count(*) FROM item c WHERE c.ancestors @> ARRAY[@run]::uuid[];", "item_subtree_idx", "(ancestors @>"),
            ("SELECT id FROM item WHERE workspace_id = @ws AND lower(ref) = lower('RUN-A');", "item_ref_lower_idx", "lower(ref) ="),
            ("SELECT id FROM item WHERE search @@ websearch_to_tsquery('english', 'quokka');", "item_search_idx", "(search @@"),
            ("SELECT id FROM note WHERE to_tsvector('english', body) @@ websearch_to_tsquery('english', 'quokka');",
                "note_search_idx", "(to_tsvector("),
        ];

        try
        {
            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
            }

            // Enough rows that each index is the cheap path whenever the planner may take it.
            await using (var owner = new NpgsqlConnection(connectionString))
            {
                await owner.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@ws, 'rls-guard');
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (@project, @ws, 'project', 'guard', 1, 'guard');
                    INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title)
                    VALUES (@run, @ws, @project, 'run', 'run-a', 1, 'run a');
                    INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title)
                    SELECT gen_random_uuid(), @ws, @project, 'followup', 'bulk-' || n, 10 + n, 'bulk ' || n
                    FROM   generate_series(1, 2000) AS n;
                    INSERT INTO note (id, workspace_id, kind, body)
                    SELECT gen_random_uuid(), @ws, 'observation', 'bulk note ' || n
                    FROM   generate_series(1, 2000) AS n;
                    ANALYZE;
                    """,
                    new { ws, project, run });
            }

            await using var source = WorkStateDataSource.Create(connectionString);
            var connections = new WorkStateConnections(source);
            using var scope = TenantScope.Enter(ws);
            foreach (var (sql, index, condition) in reads)
            {
                await using var tenant = await connections.TenantAsync(Ct);
                await tenant.Connection.ExecuteAsync("SET LOCAL enable_seqscan = off;");
                var plan = (await tenant.Connection.QueryAsync<string>($"EXPLAIN (COSTS OFF) {sql}", new { run, ws })).ToList();

                Assert.True(
                    plan.Any(line => line.Contains(index, StringComparison.Ordinal))
                    && plan.Any(line => line.Contains("Index Cond:", StringComparison.Ordinal) && line.Contains(condition, StringComparison.Ordinal)),
                    $"{index} does not serve {condition} as the tenant role:\n{string.Join("\n", plan)}");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>
    /// The image's superuser owns the tables and passes every policy, FORCE or not. FORCE is what keeps a
    /// non-superuser owner in: a deployment whose owner is not a superuser is confined like the tenant role.
    /// </summary>
    [Fact]
    public async Task Rls_force_confines_a_table_owner_that_is_not_a_superuser()
    {
        var a = await RlsWorld.CreateAsync(fixture);
        await RlsWorld.CreateAsync(fixture);
        var role = $"rls_owner_{Guid.CreateVersion7():n}";

        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await owner.BeginTransactionAsync(Ct);
        await owner.ExecuteAsync(
            $"""
             CREATE ROLE {role} NOLOGIN;
             ALTER TABLE item OWNER TO {role};
             GRANT SELECT ON workspace TO {role};
             SELECT set_config('role', '{role}', true), set_config('{WorkStateConnections.WorkspaceSetting}', @a, true);
             """,
            new { a = a.WorkspaceId.ToString() });

        Assert.Equal(0, await owner.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM item WHERE workspace_id <> @a;", new { a = a.WorkspaceId }));

        await transaction.RollbackAsync(Ct);
    }

    /// <summary>
    /// The catalogue, so a table added later cannot slip past: every table is a tenant table with its policy
    /// forced, a global one the request path may read whole, or the server's own; every view runs as its caller.
    /// </summary>
    [Fact]
    public async Task Rls_every_tenant_table_is_forced_every_other_table_is_classified_and_every_view_runs_as_its_caller()
    {
        string[] global = ["app_user", "item_kind"];
        var tenant = TenantTables.Direct.Keys.Concat(TenantTables.ThroughParent.Keys).ToHashSet();
        var ownerOnly = TenantRls.OwnerOnly.Select(t => t.Trim('"')).ToHashSet();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var tables = (await connection.QueryAsync<(string Name, bool Enabled, bool Forced, bool HasWorkspace, long Policies, bool Granted)>(
            $"""
             SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                    EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname LIKE '%workspace_id' AND NOT a.attisdropped),
                    (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = '{TenantRls.Policy}'),
                    has_table_privilege('{WorkStateConnections.AppRole}', c.oid, 'SELECT')
             FROM   pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE  n.nspname = 'public' AND c.relkind = 'r';
             """)).ToList();

        Assert.True(tables.Count > 25, $"found only {tables.Count} tables");
        foreach (var table in tables)
        {
            if (tenant.Contains(table.Name))
            {
                Assert.True(table is { Enabled: true, Forced: true, Policies: 1, Granted: true }, $"{table.Name}: {table}");
            }
            else if (global.Contains(table.Name))
            {
                Assert.True(table is { Enabled: false, HasWorkspace: false, Granted: true }, $"{table.Name}: {table}");
            }
            else
            {
                Assert.True(ownerOnly.Contains(table.Name), $"{table.Name} is neither a tenant table, a global one nor the server's own");
                Assert.True(table is { Enabled: false, HasWorkspace: false, Granted: false }, $"{table.Name}: {table}");
            }
        }

        var views = (await connection.QueryAsync<(string Name, bool Invoker)>(
            """
            SELECT c.relname, coalesce('security_invoker=true' = ANY (c.reloptions), false)
            FROM   pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE  n.nspname = 'public' AND c.relkind IN ('v', 'm');
            """)).ToList();

        Assert.Equal(TenantRls.Views.Order(), views.Select(v => v.Name).Order());
        Assert.All(views, view => Assert.True(view.Invoker, $"{view.Name} runs as its owner, which no policy confines"));
    }

    /// <summary>Every tenant table and the predicate that names tenant A's rows in it, as the owner reads them.</summary>
    private static IEnumerable<(string Table, string Predicate)> Tables()
        => TenantTables.Direct.Select(t => (t.Key, $"{t.Value} = @tenant"))
            .Concat(TenantTables.ThroughParent.Select(t => (t.Key, t.Value.Replace(TenantRls.Tenant, "@tenant"))));

    private static async Task<(int Backend, string User, string? Workspace)> SettingsAsync(NpgsqlConnection connection)
        => await connection.QuerySingleAsync<(int, string, string?)>(
            $"SELECT pg_backend_pid(), current_user::text, current_setting('{WorkStateConnections.WorkspaceSetting}', true);");
}

/// <summary>A workspace with a row in every tenant table, written as the owner.</summary>
internal sealed record RlsWorld(Guid WorkspaceId, Guid ProjectId, Guid RunId)
{
    public const string UserPrefix = "rls-seed-";

    public static async Task<RlsWorld> CreateAsync(PostgresFixture fixture)
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var tag = Guid.CreateVersion7().ToString("n");

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO app_user (id, name, password_hash) VALUES (@user, @userName, 'x');
            INSERT INTO tenant_membership (user_id, workspace_id, role) VALUES (@user, @ws, 'member');
            INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@project, @user, @ws);
            INSERT INTO user_link (id, kind, token_hash, workspace_id, role, expires_at)
            VALUES (gen_random_uuid(), 'invite', 'rls-' || @tag, @ws, 'member', now() + interval '1 day');
            INSERT INTO agent_session (id, workspace_id, name, role) VALUES (gen_random_uuid(), @ws, 'rls', 'worker');
            INSERT INTO agent_token (id, workspace_id, role, agent_name, token_hash)
            VALUES (gen_random_uuid(), @ws, 'worker', 'rls', decode(md5(@tag), 'hex'));

            INSERT INTO item_criterion (id, item_id, seq, text, checked) VALUES (gen_random_uuid(), @run, 1, 'c', false);
            INSERT INTO attempt (id, item_id, n) VALUES (@attempt, @run, 1);
            INSERT INTO report (id, attempt_id, status) VALUES (gen_random_uuid(), @attempt, 'DONE');
            INSERT INTO artifact (id, item_id, kind, value) VALUES (gen_random_uuid(), @run, 'commit', @tag);
            INSERT INTO claim (id, item_id, serialization_key, agent_name, role, expires_at)
            VALUES (gen_random_uuid(), @run, 'rls-' || @tag, 'rls', 'worker', now() + interval '1 hour');
            INSERT INTO run_config (item_id, project_item_id, serialization_key) VALUES (@run, @project, 'rls-' || @tag);
            INSERT INTO project_config (item_id, repo_root) VALUES (@project, '/rls');
            INSERT INTO project_repo (id, workspace_id, project_item_id, kind, key)
            VALUES (gen_random_uuid(), @ws, @project, 'remote', 'rls-' || @tag);

            INSERT INTO doc (id, workspace_id, slug, title) VALUES (@doc, @ws, 'rls', 'rls');
            INSERT INTO doc_block (id, doc_id, seq, short_id, body) VALUES (@block, @doc, 1, 'b1', 'v1');
            UPDATE doc_block SET body = 'v2' WHERE id = @block;

            INSERT INTO item_link (id, workspace_id, from_item_id, to_item_id, type)
            VALUES (gen_random_uuid(), @ws, @run, @other, 'blocks');
            INSERT INTO note (id, workspace_id, kind, body, to_item_id) VALUES (@note, @ws, 'observation', 'rls', @run);
            INSERT INTO assumption (id, workspace_id, scope_item_id, ref, statement, evidence)
            VALUES (gen_random_uuid(), @ws, @project, 'A-rls', 'rls', 'asserted');
            INSERT INTO constraint_rule (id, workspace_id, kind, text) VALUES (gen_random_uuid(), @ws, 'red_line', 'rls');
            INSERT INTO verification (id, workspace_id, item_id, result_line, passed, run_by)
            VALUES (gen_random_uuid(), @ws, @run, 'ok', true, 'worker');
            INSERT INTO event (workspace_id, actor, role, kind) VALUES (@ws, 'rls', 'system', 'rls_seed');
            INSERT INTO import_record (id, workspace_id, project_item_id, source_path, kind, why, imported_by, note_id)
            VALUES (gen_random_uuid(), @ws, @project, 'rls.md', 'note', 'rls', 'rls', @note);
            """,
            new
            {
                ws = world.WorkspaceId,
                project = world.ProjectId,
                run = world.RunA,
                other = world.RunB,
                user = Guid.CreateVersion7(),
                attempt = Guid.CreateVersion7(),
                doc = Guid.CreateVersion7(),
                block = Guid.CreateVersion7(),
                note = Guid.CreateVersion7(),
                userName = UserPrefix + tag,
                tag,
            });

        return new RlsWorld(world.WorkspaceId, world.ProjectId, world.RunA);
    }
}

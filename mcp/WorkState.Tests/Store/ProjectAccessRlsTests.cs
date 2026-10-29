using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using WorkState.Core.Data;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// doc 02 §15b in the database: as RlsTests proves the tenant, every query here names the other project's rows
/// outright and leaves membership to the policies. The two projects of one tenant hold a row in every table that
/// belongs to a project; a user who is a member of the first sees none of the second's, a member of both sees both,
/// and a scope with no user sees the tenant whole.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectAccessRlsTests(PostgresFixture fixture) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        await owner.ExecuteAsync($"DELETE FROM app_user WHERE name LIKE '{RlsWorld.UserPrefix}%';");
    }

    [Fact]
    public async Task ProjectAccess_an_unfiltered_query_through_the_tenant_helper_as_a_non_member_returns_none_of_the_projects_rows()
    {
        var world = await RlsWorld.CreateAsync(fixture);
        var (one, two) = (await UserAsync(world.WorkspaceId), await UserAsync(world.WorkspaceId));
        var mine = await ProjectAsync(world.WorkspaceId, one: one, also: two);
        var other = await ProjectAsync(world.WorkspaceId, one: two, also: null);

        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        var connections = new WorkStateConnections(source);
        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        var tables = Tables();

        Assert.Equal(
            TenantTables.ByProject.Keys.Concat(TenantTables.ThroughParent.Keys).Order(),
            tables.Keys.Order());

        foreach (var (table, predicate) in tables)
        {
            var args = new { project = other.ProjectId, tenant = world.WorkspaceId };
            var stored = await owner.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {predicate};", args);
            Assert.True(stored > 0, $"{table}: the seed left the other project without a row, so this proves nothing");

            Assert.True(await SeenAsync(world.WorkspaceId, one) == 0, $"{table}: a non-member sees the other project's rows");
            Assert.True(await SeenAsync(world.WorkspaceId, two) == stored, $"{table}: a member does not see all of their project's rows");
            Assert.True(await SeenAsync(world.WorkspaceId, null) == stored, $"{table}: a scope with no user does not see the tenant whole");

            // Their own project's rows are there for the non-member: the policy hides a project, not a table.
            var own = new { project = mine.ProjectId, tenant = world.WorkspaceId };
            using (TenantScope.EnterAs(world.WorkspaceId, one))
            await using (var tenant = await connections.TenantAsync(Ct))
            {
                Assert.True(
                    await tenant.Connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {predicate};", own) > 0,
                    $"{table}: a member of the first project does not see its rows");
            }

            async Task<long> SeenAsync(Guid workspace, Guid? user)
            {
                using var scope = TenantScope.EnterAs(workspace, user);
                await using var tenant = await connections.TenantAsync(Ct);
                return await tenant.Connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {predicate};", args);
            }
        }

        // The views run as their caller, and so under the same policies.
        using (TenantScope.EnterAs(world.WorkspaceId, one))
        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.Equal(0, await tenant.Connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM item_rollup WHERE id = @project;", new { project = other.ProjectId }));
            Assert.Equal(0, await tenant.Connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM open_thread WHERE scope = ANY(@ids);", new { ids = other.Ids }));
        }
    }

    [Fact]
    public async Task ProjectAccess_a_non_member_cannot_write_into_the_project_and_its_rows_cannot_be_touched()
    {
        var world = await RlsWorld.CreateAsync(fixture);
        var (one, two) = (await UserAsync(world.WorkspaceId), await UserAsync(world.WorkspaceId));
        var other = await ProjectAsync(world.WorkspaceId, one: two, also: null);
        await MemberAsync(world.WorkspaceId, world.ProjectId, one);

        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        var connections = new WorkStateConnections(source);
        using var scope = TenantScope.EnterAs(world.WorkspaceId, one);

        string[] refused =
        [
            "INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES (gen_random_uuid(), @ws, @run, 'task', 'pa-x', 99, 'x');",
            "INSERT INTO item_criterion (id, item_id, seq, text, checked) VALUES (gen_random_uuid(), @run, 99, 'x', false);",
            "INSERT INTO note (id, workspace_id, kind, body, to_item_id) VALUES (gen_random_uuid(), @ws, 'observation', 'x', @run);",
            "INSERT INTO event (workspace_id, item_id, actor, role, kind) VALUES (@ws, @run, 'pa', 'system', 'pa_probe');",
            "INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@project, @one, @ws);",
            "UPDATE item SET parent_id = @run WHERE id = @mine;",
        ];

        var args = new { ws = world.WorkspaceId, run = other.RunId, project = other.ProjectId, one, mine = world.RunId };
        foreach (var statement in refused)
        {
            await using var tenant = await connections.TenantAsync(Ct);
            var error = await Seed.RefusedAsync(() => tenant.Connection.ExecuteAsync(statement, args));
            Assert.True(
                error.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ForeignKeyViolation,
                $"{statement} → {error.SqlState} {error.MessageText}");
        }

        await using (var tenant = await connections.TenantAsync(Ct))
        {
            Assert.Equal(0, await tenant.Connection.ExecuteAsync("UPDATE item SET title = 'taken' WHERE id = ANY(@ids);", new { ids = other.Ids }));
            Assert.Equal(0, await tenant.Connection.ExecuteAsync("DELETE FROM item_criterion WHERE item_id = @run;", args));
            Assert.Equal(0, await tenant.Connection.ExecuteAsync("DELETE FROM project_member WHERE project_item_id = @project;", args));
            await tenant.CommitAsync(Ct);
        }

        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        Assert.Equal(0, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM item WHERE id = ANY(@ids) AND title = 'taken';", new { ids = other.Ids }));
        Assert.Equal(1, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM item_criterion WHERE item_id = @run;", args));
        Assert.Equal(1, await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM project_member WHERE project_item_id = @project;", args));
    }

    /// <summary>
    /// The first member makes a project private, from the next statement on; a project no one has joined, and a root
    /// that is no project, are the tenant's.
    /// </summary>
    [Fact]
    public async Task ProjectAccess_a_project_with_no_member_and_a_parentless_feature_are_visible_to_the_tenant_until_a_member_joins()
    {
        var world = await RlsWorld.CreateAsync(fixture);
        var (one, two) = (await UserAsync(world.WorkspaceId), await UserAsync(world.WorkspaceId));
        var unjoined = await ProjectAsync(world.WorkspaceId, one: null, also: null);
        var feature = Guid.CreateVersion7();
        await using (var owner = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await owner.ExecuteAsync(
                "INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (@feature, @ws, 'feature', @tag, 77, 'loose');",
                new { feature, ws = world.WorkspaceId, tag = $"f-{feature:n}"[..12] });
        }

        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        var connections = new WorkStateConnections(source);

        Assert.Equal(unjoined.Ids.Length + 1, await CountAsync(one));
        await MemberAsync(world.WorkspaceId, unjoined.ProjectId, two);
        Assert.Equal(1, await CountAsync(one));
        Assert.Equal(unjoined.Ids.Length + 1, await CountAsync(two));

        async Task<long> CountAsync(Guid user)
        {
            using var scope = TenantScope.EnterAs(world.WorkspaceId, user);
            await using var tenant = await connections.TenantAsync(Ct);
            return await tenant.Connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM item WHERE id = ANY(@ids) OR id = @feature;",
                new { ids = unjoined.Ids, feature });
        }
    }

    /// <summary>
    /// The request paths enter their scope with the principal's user (<c>EnterAs</c>); <c>Enter</c>, which has none
    /// and so sees every project, is the test suite's.
    /// </summary>
    [Fact]
    public void ProjectAccess_every_tenant_scope_the_server_enters_names_its_user()
    {
        var root = Root();
        var sources = Directory.EnumerateDirectories(root, "WorkState.*")
            .Where(project => !Path.GetFileName(project).Equals("WorkState.Tests", StringComparison.Ordinal))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.EndsWith(Path.Combine("Data", "TenantScope.cs"), StringComparison.Ordinal))
            .Select(file => (File: Path.GetRelativePath(root, file), Text: System.IO.File.ReadAllText(file)))
            .ToList();

        var named = sources.Sum(source => Regex.Count(source.Text, @"TenantScope\.EnterAs\("));
        var offenders = sources.Where(source => Regex.IsMatch(source.Text, @"TenantScope\.Enter\(")).Select(source => source.File).ToList();

        Assert.True(named >= 4, $"found only {named} TenantScope.EnterAs calls in the server");
        Assert.True(offenders.Count == 0, $"a scope entered without its user: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// (a) of the fail-closed three: U1 holds one transaction open while another removes them from a project they
    /// could see when it began. Their next statement no longer can — the seq moved, so the policy asks the live check.
    /// </summary>
    [Fact]
    public async Task ProjectAccess_a_member_removed_while_their_transaction_is_open_sees_none_of_it_from_their_next_statement()
    {
        var world = await RlsWorld.CreateAsync(fixture);
        var (one, two) = (await UserAsync(world.WorkspaceId), await UserAsync(world.WorkspaceId));
        var project = await ProjectAsync(world.WorkspaceId, one: one, also: two);

        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        using var scope = TenantScope.EnterAs(world.WorkspaceId, one);
        await using var held = await new WorkStateConnections(source).TenantAsync(Ct);

        Assert.Equal(3, await ItemsAsync(held, project.Ids));
        Assert.Equal(1, await NotesAsync(held, project.TaskId));

        await CommitAsOwnerAsync(
            "DELETE FROM project_member WHERE project_item_id = @project AND user_id = @one;",
            new { project = project.ProjectId, one });

        Assert.Equal(0, await ItemsAsync(held, project.Ids));
        Assert.Equal(0, await NotesAsync(held, project.TaskId));
    }

    /// <summary>
    /// (b): a project created, with its members and a note, while U1's transaction is open — U1 is none of them, and
    /// nothing was hidden from U1 when it began, so even the rows that skip the item lookup must not show it.
    /// </summary>
    [Fact]
    public async Task ProjectAccess_a_project_created_while_a_non_members_transaction_is_open_is_never_visible_to_them()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var (one, two) = (await UserAsync(world.WorkspaceId), await UserAsync(world.WorkspaceId));
        var (project, run, note) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        using var scope = TenantScope.EnterAs(world.WorkspaceId, one);
        await using var held = await new WorkStateConnections(source).TenantAsync(Ct);

        Assert.Equal("{}", await held.Connection.ExecuteScalarAsync<string>(
            $"SELECT current_setting('{WorkStateConnections.HiddenSetting}');"));
        Assert.True(await ItemsAsync(held, [world.ProjectId, world.RunA]) == 2, "the workspace's own project is not visible");

        await CommitAsOwnerAsync(
            """
            INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (@project, @ws, 'project', 'pa-' || right(@tag, 8), 60, 'new');
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES (@run, @ws, @project, 'run', 'run-a', 1, 'run');
            INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@project, @two, @ws);
            INSERT INTO note (id, workspace_id, kind, body, to_item_id) VALUES (@note, @ws, 'observation', 'pa', @run);
            """,
            new { ws = world.WorkspaceId, project, run, note, two, tag = Guid.CreateVersion7().ToString("n") });

        Assert.Equal(0, await ItemsAsync(held, [project, run]));
        Assert.Equal(0, await NotesAsync(held, run));
        await held.CommitAsync(Ct);

        using (TenantScope.EnterAs(world.WorkspaceId, one))
        await using (var next = await new WorkStateConnections(source).TenantAsync(Ct))
        {
            Assert.Equal(0, await ItemsAsync(next, [project, run]));
            Assert.Equal(0, await NotesAsync(next, run));
        }

        using (TenantScope.EnterAs(world.WorkspaceId, two))
        await using (var member = await new WorkStateConnections(source).TenantAsync(Ct))
        {
            Assert.Equal(2, await ItemsAsync(member, [project, run]));
            Assert.Equal(1, await NotesAsync(member, run));
        }
    }

    /// <summary>(c): U1 added to a project while their transaction is open sees it from their next statement on.</summary>
    [Fact]
    public async Task ProjectAccess_a_member_added_while_their_transaction_is_open_sees_the_project_from_their_next_statement()
    {
        var world = await RlsWorld.CreateAsync(fixture);
        var (one, two) = (await UserAsync(world.WorkspaceId), await UserAsync(world.WorkspaceId));
        var project = await ProjectAsync(world.WorkspaceId, one: two, also: null);

        await using var source = WorkStateDataSource.Create(fixture.ConnectionString);
        using var scope = TenantScope.EnterAs(world.WorkspaceId, one);
        await using var held = await new WorkStateConnections(source).TenantAsync(Ct);

        Assert.Equal(0, await ItemsAsync(held, project.Ids));

        await CommitAsOwnerAsync(
            "INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@project, @one, @ws);",
            new { project = project.ProjectId, one, ws = world.WorkspaceId });

        Assert.Equal(3, await ItemsAsync(held, project.Ids));
        Assert.Equal(1, await NotesAsync(held, project.TaskId));
    }

    private static Task<long> ItemsAsync(OpenTransaction tenant, Guid[] ids)
        => tenant.Connection.ExecuteScalarAsync<long>("SELECT count(*) FROM item WHERE id = ANY(@ids);", new { ids }, tenant.Transaction);

    private static Task<long> NotesAsync(OpenTransaction tenant, Guid item)
        => tenant.Connection.ExecuteScalarAsync<long>("SELECT count(*) FROM note WHERE to_item_id = @item;", new { item }, tenant.Transaction);

    /// <summary>Another connection's change, committed while the test's tenant transaction stays open.</summary>
    private async Task CommitAsOwnerAsync(string sql, object args)
    {
        await using var owner = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await owner.BeginTransactionAsync(Ct);
        await owner.ExecuteAsync(sql, args, transaction);
        await transaction.CommitAsync(Ct);
    }

    /// <summary>Every table whose rows belong to a project, and the owner's predicate naming one project's rows in it.</summary>
    private static Dictionary<string, string> Tables()
    {
        const string tree = "(SELECT t.id FROM item t WHERE t.id = @project OR t.ancestors @> ARRAY[@project]::uuid[])";
        return new Dictionary<string, string>
        {
            ["item"] = $"id IN {tree}",
            ["project_member"] = "project_item_id = @project",
            ["item_link"] = $"from_item_id IN {tree} OR to_item_id IN {tree}",
            ["doc"] = $"item_id IN {tree}",
            ["event"] = $"item_id IN {tree}",
            ["assumption"] = $"scope_item_id IN {tree}",
            ["constraint_rule"] = $"scope_item_id IN {tree}",
            ["note"] = $"to_item_id IN {tree} OR from_item_id IN {tree}",
            ["verification"] = $"item_id IN {tree}",
            ["project_repo"] = "project_item_id = @project",
            ["agent_token"] = $"scope_item_id IN {tree}",
            ["import_record"] = "project_item_id = @project",
            ["item_criterion"] = $"item_id IN {tree}",
            ["attempt"] = $"item_id IN {tree}",
            ["artifact"] = $"item_id IN {tree}",
            ["claim"] = $"item_id IN {tree}",
            ["project_config"] = $"item_id IN {tree}",
            ["run_config"] = $"item_id IN {tree}",
            ["report"] = $"attempt_id IN (SELECT a.id FROM attempt a WHERE a.item_id IN {tree})",
            ["doc_block"] = $"doc_id IN (SELECT d.id FROM doc d WHERE d.item_id IN {tree})",
            ["doc_block_revision"] =
                $"block_id IN (SELECT b.id FROM doc_block b JOIN doc d ON d.id = b.doc_id WHERE d.item_id IN {tree})",
        };
    }

    private sealed record OtherProject(Guid ProjectId, Guid RunId, Guid TaskId)
    {
        public Guid[] Ids => [ProjectId, RunId, TaskId];
    }

    /// <summary>A second project in the workspace, with a row in every project table; its one member, if any.</summary>
    private async Task<OtherProject> ProjectAsync(Guid workspace, Guid? one, Guid? also)
    {
        var (project, run, task) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (attempt, doc, block, note) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var tag = Guid.CreateVersion7().ToString("n");

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (@project, @ws, 'project', 'pa-' || right(@tag, 8), 50, 'other');
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title) VALUES (@run, @ws, @project, 'run', 'run-a', 1, 'run');
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, verify_cmd)
            VALUES (@task, @ws, @run, 'task', '1', 1, 'task', 'dotnet test');

            INSERT INTO item_criterion (id, item_id, seq, text, checked) VALUES (gen_random_uuid(), @run, 1, 'c', false);
            INSERT INTO attempt (id, item_id, n) VALUES (@attempt, @run, 1);
            INSERT INTO report (id, attempt_id, status) VALUES (gen_random_uuid(), @attempt, 'DONE');
            INSERT INTO artifact (id, item_id, kind, value) VALUES (gen_random_uuid(), @run, 'commit', @tag);
            INSERT INTO claim (id, item_id, serialization_key, agent_name, role, expires_at)
            VALUES (gen_random_uuid(), @run, 'pa-' || @tag, 'pa', 'worker', now() + interval '1 hour');
            INSERT INTO run_config (item_id, project_item_id, serialization_key) VALUES (@run, @project, 'pa-' || @tag);
            INSERT INTO project_config (item_id, repo_root) VALUES (@project, '/pa');
            INSERT INTO project_repo (id, workspace_id, project_item_id, kind, key) VALUES (gen_random_uuid(), @ws, @project, 'remote', 'pa-' || @tag);
            INSERT INTO doc (id, workspace_id, item_id, slug, title) VALUES (@doc, @ws, @task, 'pa-' || @tag, 'pa');
            INSERT INTO doc_block (id, doc_id, seq, short_id, body) VALUES (@block, @doc, 1, 'b1', 'v1');
            UPDATE doc_block SET body = 'v2' WHERE id = @block;
            INSERT INTO item_link (id, workspace_id, from_item_id, to_item_id, type) VALUES (gen_random_uuid(), @ws, @task, @run, 'blocks');
            INSERT INTO note (id, workspace_id, kind, body, to_item_id) VALUES (@note, @ws, 'observation', 'pa', @task);
            INSERT INTO assumption (id, workspace_id, scope_item_id, ref, statement, evidence)
            VALUES (gen_random_uuid(), @ws, @run, 'A-pa', 'pa', 'asserted');
            INSERT INTO constraint_rule (id, workspace_id, scope_item_id, ref, kind, text)
            VALUES (gen_random_uuid(), @ws, @project, 'C-' || right(@tag, 10), 'stack_fact', 'pa');
            INSERT INTO verification (id, workspace_id, item_id, result_line, passed, run_by) VALUES (gen_random_uuid(), @ws, @task, 'ok', true, 'worker');
            INSERT INTO event (workspace_id, item_id, actor, role, kind) VALUES (@ws, @task, 'pa', 'system', 'pa_seed');
            INSERT INTO agent_token (id, workspace_id, scope_item_id, role, agent_name, token_hash)
            VALUES (gen_random_uuid(), @ws, @project, 'worker', 'pa', decode(md5(@tag), 'hex'));
            INSERT INTO import_record (id, workspace_id, project_item_id, source_path, kind, why, imported_by, note_id)
            VALUES (gen_random_uuid(), @ws, @project, 'pa.md', 'note', 'pa', 'pa', @note);
            """,
            new { ws = workspace, project, run, task, attempt, doc, block, note, tag });

        foreach (var user in new[] { one, also }.OfType<Guid>())
        {
            await MemberAsync(workspace, project, user);
        }

        return new OtherProject(project, run, task);
    }

    private async Task<Guid> UserAsync(Guid workspace)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO app_user (id, name, password_hash) VALUES (@id, @name, 'x');
            INSERT INTO tenant_membership (user_id, workspace_id, role) VALUES (@id, @workspace, 'member');
            """,
            new { id, name = $"{RlsWorld.UserPrefix}pa-{id:n}", workspace });
        return id;
    }

    private async Task MemberAsync(Guid workspace, Guid project, Guid user)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO project_member (project_item_id, user_id, workspace_id) VALUES (@project, @user, @workspace)
            ON CONFLICT DO NOTHING;
            """,
            new { project, user, workspace });
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WorkState.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no WorkState.slnx above {AppContext.BaseDirectory}");
    }
}

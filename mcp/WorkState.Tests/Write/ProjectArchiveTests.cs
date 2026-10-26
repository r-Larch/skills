using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Api.App;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.App;
using WorkState.Tests.App.V1;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// Archiving hides a project and deletes nothing: an orchestrator archives through
/// <c>work_update(archived:)</c>, the operator through the dashboard's one write endpoint; an archived
/// project still opens by ref and by repo, and new work under it brings it back.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectArchiveTests(PostgresFixture fixture)
{
    private const string BeforeArchive = "20261026102304_ProjectRepos";

    private const string Remote = "git@github.com:r-Larch/Archived.git";

    [Fact]
    public async Task Work_update_archives_and_unarchives_a_project_and_journals_each_change_once()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var archived = await ReadServer.CallAsync(client, "work_update", Args(project, true), Ct);
        Assert.Contains("archived — hidden from the dashboard's lists", archived, StringComparison.Ordinal);
        var at = await ArchivedAtAsync(world.ProjectId);
        Assert.NotNull(at);

        var again = await ReadServer.CallAsync(client, "work_update", Args(project, true), Ct);
        Assert.Contains("already archived", again, StringComparison.Ordinal);
        Assert.Equal(at, await ArchivedAtAsync(world.ProjectId));

        var restored = await ReadServer.CallAsync(client, "work_update", Args(project, false), Ct);
        Assert.Contains("unarchived", restored, StringComparison.Ordinal);
        Assert.Null(await ArchivedAtAsync(world.ProjectId));

        Assert.Equal(["archived", "unarchived"], await WriteSeed.EventKindsAsync(world, world.ProjectId));
    }

    [Fact]
    public async Task Work_update_archived_on_anything_but_a_project_is_refused_with_a_route()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "run-a",
            new UpdateArgs(Archived: true), Ct);

        Assert.True(refused.Refused);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("only a project is archived", refused.Text, StringComparison.Ordinal);
        Assert.Empty(await WriteSeed.EventKindsAsync(world));
    }

    [Fact]
    public async Task The_dashboard_archives_and_unarchives_with_its_cookie_and_the_write_header()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var cookie = await server.SignInAsync(Ct);

        var archived = await PostAsync(cookie, $"/api/projects/{world.ProjectId}/archive", header: true);
        Assert.Equal(HttpStatusCode.OK, archived.Status);
        Assert.Equal(JsonValueKind.String, archived.Body.GetProperty("archivedAt").ValueKind);
        Assert.True(archived.Body.GetProperty("changed").GetBoolean());
        Assert.NotNull(await ArchivedAtAsync(world.ProjectId));

        await using (var db = fixture.NewContext())
        {
            var journalled = await db.Events.SingleAsync(e => e.ItemId == world.ProjectId && e.Kind == "archived", Ct);
            Assert.Equal(AppServer.Username, journalled.Actor);
            Assert.Equal(ActorRole.Human, journalled.Role);
        }

        var restored = await PostAsync(cookie, $"/api/projects/{world.ProjectId}/unarchive", header: true);
        Assert.Equal(HttpStatusCode.OK, restored.Status);
        Assert.Equal(JsonValueKind.Null, restored.Body.GetProperty("archivedAt").ValueKind);
        Assert.Null(await ArchivedAtAsync(world.ProjectId));

        var task = await PostAsync(cookie, $"/api/projects/{world.TaskId}/archive", header: true);
        Assert.Equal(HttpStatusCode.BadRequest, task.Status);

        var unknown = await PostAsync(cookie, $"/api/projects/{Guid.CreateVersion7()}/archive", header: true);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
    }

    /// <summary>A form post from another site carries the cookie it may be sent, never the header.</summary>
    [Fact]
    public async Task A_dashboard_write_without_the_write_header_is_refused_and_changes_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var cookie = await server.SignInAsync(Ct);

        var refused = await PostAsync(cookie, $"/api/projects/{world.ProjectId}/archive", header: false);

        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains(ProjectEndpoints.WriteHeader, refused.Body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Null(await ArchivedAtAsync(world.ProjectId));
        Assert.Empty(await WriteSeed.EventKindsAsync(world, world.ProjectId));
    }

    [Fact]
    public async Task An_agent_token_or_no_credential_cannot_use_the_dashboard_write()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        foreach (var token in (string[])[ReadServer.WorkerToken, ReadServer.ControllerToken])
        {
            var bearer = V1Http.Bearer(server, token);
            var refused = await PostAsync(bearer, $"/api/projects/{world.ProjectId}/archive", header: true);
            Assert.True(
                refused.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"archive with a bearer token → {(int)refused.Status}");
        }

        var anonymous = await PostAsync(server.NewClient(), $"/api/projects/{world.ProjectId}/archive", header: true);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);

        Assert.Null(await ArchivedAtAsync(world.ProjectId));
    }

    [Fact]
    public async Task An_archived_project_leaves_the_default_lists_but_still_opens_by_ref_and_by_repo()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateArchivableAsync(world);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        // Two live projects: a bare brief opens neither's run until one is archived (bare-brief).
        Assert.Equal("RUN —  ·  repo not given, and this workspace holds 2 projects", await FirstLineAsync(client, null));

        Assert.False((await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "arch",
            new UpdateArgs(Archived: true), Ct)).Refused);

        var bare = await FirstLineAsync(client, null);
        Assert.Contains("run-a", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("archived", bare, StringComparison.Ordinal);

        var byRepo = await FirstLineAsync(client, new Dictionary<string, object?>
        {
            ["repo"] = new Dictionary<string, object?> { ["remote"] = "https://github.com/r-larch/archived" },
        });
        Assert.StartsWith("PROJECT arch ", byRepo, StringComparison.Ordinal);
        Assert.Matches(@"  ·  archived \d{4}-\d{2}-\d{2}$", byRepo);

        var byRef = await FirstLineAsync(client, new Dictionary<string, object?> { ["scope"] = "arch" });
        Assert.Matches(@"  ·  archived \d{4}-\d{2}-\d{2}$", byRef);

        var onboarding = await new BoardQueries(fixture.DataSource).ProjectRefsAsync(world.WorkspaceId, Ct);
        Assert.DoesNotContain("arch", onboarding);
        Assert.Contains(await ProjectRefAsync(world), onboarding);
    }

    [Fact]
    public async Task Work_create_under_an_archived_project_unarchives_it_and_journals_why()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        Assert.False((await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Archived: true), Ct)).Refused);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "New work", Parent: "2", VerifyCmd: "dotnet test", Criteria: ["it works"])], Ct);

        Assert.False(created.Refused, created.Text);
        Assert.Contains($"project {project} unarchived — new work arrived", created.Text, StringComparison.Ordinal);
        Assert.Null(await ArchivedAtAsync(world.ProjectId));

        await using var db = fixture.NewContext();
        var restored = await db.Events.SingleAsync(e => e.ItemId == world.ProjectId && e.Kind == "unarchived", Ct);
        Assert.Contains("new work 2.1 created under it", restored.Summary, StringComparison.Ordinal);
        Assert.Equal(WriteSeed.Orchestrator.Name, restored.Actor);
    }

    [Fact]
    public async Task A_projects_last_activity_is_the_newest_event_in_its_subtree_and_orders_the_list()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Guid quiet, silent;
        await using (var db = fixture.NewContext())
        {
            var q = Seed.NewItem(world.WorkspaceId, null, "project", "quiet", 2m, "Quiet");
            var s = Seed.NewItem(world.WorkspaceId, null, "project", "silent", 3m, "Silent");
            db.Items.AddRange(q, s);
            await db.SaveChangesAsync(Ct);
            (quiet, silent) = (q.Id, s.Id);
        }

        await EventAtAsync(world, world.ProjectId, days: 5);
        await EventAtAsync(world, world.TaskId, days: 3);
        await EventAtAsync(world, quiet, days: 1);

        var projects = await new BoardQueries(fixture.DataSource).ProjectsAsync(world.WorkspaceId, Ct);

        Assert.Equal([quiet, world.ProjectId, silent], projects.Select(p => p.Id));
        var mine = projects.Single(p => p.Id == world.ProjectId);
        Assert.InRange(mine.LastActivityAt!.Value, DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(5));
        Assert.Null(projects.Single(p => p.Id == silent).LastActivityAt);
        Assert.Equal(1, mine.OpenRuns);
    }

    [Fact]
    public async Task The_v1_overview_carries_archived_at_last_activity_and_open_runs()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var world = v1.World;
        await using var server = await v1.StartAsync(fixture);

        var before = Assert.Single(V1Http.Array(await V1Http.GetAsync(V1Http.Reader(server), "/api/v1/overview"), "projects"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("archivedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, before.GetProperty("lastActivityAt").ValueKind);
        Assert.Equal(1, before.GetProperty("openRuns").GetInt32());

        Assert.False((await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, await ProjectRefAsync(world),
            new UpdateArgs(Archived: true), Ct)).Refused);

        var after = Assert.Single(V1Http.Array(await V1Http.GetAsync(V1Http.Reader(server), "/api/v1/overview"), "projects"));
        Assert.Equal(JsonValueKind.String, after.GetProperty("archivedAt").ValueKind);
        Assert.True(after.GetProperty("lastActivityAt").GetDateTimeOffset() >= before.GetProperty("lastActivityAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_archive_migration_goes_down_and_up_again()
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

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title, archived_at)
                    VALUES (@project, @workspace, 'project', 'p', 1, 'P', now());
                    """,
                    new { workspace, slug = $"mig-{workspace:n}", project });

                var notAProject = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    """
                    INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, archived_at)
                    VALUES (gen_random_uuid(), @workspace, @project, 'run', 'r', 1, 'R', now());
                    """,
                    new { workspace, project }));
                Assert.Equal(PostgresErrorCodes.CheckViolation, notAProject.SqlState);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeArchive, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Empty(await connection.QueryAsync<string>(
                    "SELECT column_name FROM information_schema.columns WHERE table_name = 'item' AND column_name = 'archived_at';"));
                Assert.Equal("p", await connection.ExecuteScalarAsync<string>(
                    "SELECT ref FROM item WHERE id = @project;", new { project }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
                Assert.Null((await context.Items.SingleAsync(i => i.Id == project, Ct)).ArchivedAt);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>Project <c>arch</c>, bound to <see cref="Remote"/>, whose run carries more open work than run-a.</summary>
    private static async Task CreateArchivableAsync(WriteWorld world)
    {
        foreach (var item in (NewItemArg[])
                 [
                     new("project", "Archivable", Ref: "arch", Repo: new RepoArg(Remote)),
                     new("run", "The archivable run", Ref: "arch-run", Parent: "arch"),
                     new("phase", "Work", Ref: "a1", Parent: "arch-run"),
                 ])
        {
            var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator, [item], Ct);
            Assert.False(created.Refused, created.Text);
        }

        var tasks = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
        [
            new NewItemArg("task", "One", Parent: "arch-run/a1", VerifyCmd: "dotnet test", Criteria: ["one"]),
            new NewItemArg("task", "Two", Parent: "arch-run/a1", VerifyCmd: "dotnet test", Criteria: ["two"]),
            new NewItemArg("task", "Three", Parent: "arch-run/a1", VerifyCmd: "dotnet test", Criteria: ["three"]),
        ], Ct);
        Assert.False(tasks.Refused, tasks.Text);
    }

    private static async Task<string> FirstLineAsync(ModelContextProtocol.Client.McpClient client, Dictionary<string, object?>? arguments)
        => (await ReadServer.CallAsync(client, "work_brief", arguments, Ct)).Split('\n')[0];

    private static Dictionary<string, object?> Args(string reference, bool archived)
        => new() { ["ref"] = reference, ["archived"] = archived };

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string path, bool header)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (header)
        {
            request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<DateTimeOffset?> ArchivedAtAsync(Guid projectId)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == projectId, Ct)).ArchivedAt;
    }

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private async Task EventAtAsync(WriteWorld world, Guid itemId, int days)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, at)
            VALUES (@workspace, @item, 'orchestrator-a', 'orchestrator', 'updated', '{}', 'seeded', now() - make_interval(days => @days));
            """,
            new { workspace = world.WorkspaceId, item = itemId, days });
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.App;
using WorkState.Tests.App.V1;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// One project per repository. A repo is known by its normalised origin remote and its main
/// checkout's root; either key finds its project, a key belongs to one project, and a session that
/// names its repo — or was minted with one — opens that project without being told its ref.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RepoBindingTests(PostgresFixture fixture)
{
    private const string BeforeRepos = "20261026072032_TokenSlidingExpiry";

    private const string Remote = "git@github.com:r-Larch/Other.git";
    private const string Root = @"P:\Projects\LarchSys\Other";

    [Fact]
    public void Every_row_of_the_identity_table_normalises_as_documented()
    {
        using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "repo-keys.json")));
        var wrong = new List<string>();

        foreach (var (kind, normalise) in new (string, Func<string?, string?>)[]
                 {
                     ("remote", RepoKeys.NormaliseRemote),
                     ("root", RepoKeys.NormaliseRoot),
                 })
        {
            var rows = table.RootElement.GetProperty(kind).EnumerateArray().ToList();
            Assert.NotEmpty(rows);

            foreach (var row in rows)
            {
                var input = row[0].GetString();
                var expected = row[1].ValueKind == JsonValueKind.Null ? null : row[1].GetString();
                var actual = normalise(input);
                if (actual != expected)
                {
                    wrong.Add($"{kind} \"{input}\" → \"{actual}\", expected \"{expected}\"");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public async Task A_project_created_with_repo_binds_both_keys_normalised()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var result = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "The other repo", Ref: "other", Repo: new RepoArg(Remote, Root))], Ct);

        Assert.False(result.Refused, result.Text);
        Assert.Contains("repo      remote github.com/r-larch/other, root p:/projects/larchsys/other", result.Text, StringComparison.Ordinal);

        var keys = await KeysAsync(world, "other");
        Assert.Equal(["remote github.com/r-larch/other", "root p:/projects/larchsys/other"], keys);
    }

    /// <summary>The scp form and the https form are one repository, so the second project is refused.</summary>
    [Fact]
    public async Task A_second_project_for_a_bound_repo_is_refused_naming_the_first()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateOtherAsync(world);

        var again = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "A duplicate", Ref: "dupe", Repo: new RepoArg("https://github.com/r-larch/other"))], Ct);

        Assert.True(again.Refused);
        RefusalShape.SelfCorrecting(again.Text);
        Assert.Contains("this repo is project other", again.Text, StringComparison.Ordinal);
        Assert.Contains("work_brief(scope:\"other\")", again.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.False(await db.Items.AnyAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "dupe", Ct));
    }

    [Fact]
    public async Task Only_a_project_takes_a_repo()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("phase", "A phase", Parent: "run-a", Repo: new RepoArg(Remote))], Ct);
        var updated = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "run-a",
            new UpdateArgs(Repo: new RepoArg(Remote)), Ct);

        Assert.True(created.Refused);
        Assert.True(updated.Refused);
        RefusalShape.SelfCorrecting(updated.Text);
        Assert.Contains("repo: binds a project", updated.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_update_adds_a_repo_to_a_project_and_refuses_one_another_project_holds()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateOtherAsync(world);
        var project = await ProjectRefAsync(world);

        var added = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg("https://github.com/r-Larch/skills.git", "P:/Projects/LarchSys/Skills/")), Ct);
        Assert.False(added.Refused, added.Text);
        Assert.Contains("— bound", added.Text, StringComparison.Ordinal);

        var second = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg(Root: "p:/projects/larchsys/skills2")), Ct);
        Assert.False(second.Refused, second.Text);

        var again = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg("git@github.com:r-Larch/skills")), Ct);
        Assert.False(again.Refused, again.Text);
        Assert.Contains("already this project's", again.Text, StringComparison.Ordinal);

        Assert.Equal(
            ["remote github.com/r-larch/skills", "root p:/projects/larchsys/skills", "root p:/projects/larchsys/skills2"],
            await KeysAsync(world, project));

        var taken = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg(Root: Root)), Ct);
        Assert.True(taken.Refused);
        Assert.Contains("this repo is project other", taken.Text, StringComparison.Ordinal);
        Assert.Equal(3, (await KeysAsync(world, project)).Count);
    }

    [Fact]
    public async Task Work_brief_with_repo_opens_that_projects_brief_and_work_get_shows_its_repos()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateOtherAsync(world);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var bare = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        Assert.Contains("run-a", bare.Split('\n')[0], StringComparison.Ordinal);

        var byRemote = await ReadServer.CallAsync(client, "work_brief", Repo(remote: "https://github.com/r-Larch/Other"), Ct);
        Assert.StartsWith("PROJECT other  ·  The other repo", byRemote.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("\nNEXT   resume run other-run — work_brief(scope:\"other-run\")", byRemote, StringComparison.Ordinal);
        Assert.DoesNotContain("repo     not given", byRemote, StringComparison.Ordinal);

        var byRoot = await ReadServer.CallAsync(client, "work_brief", Repo(root: "p:/projects/larchsys/other/.git"), Ct);
        Assert.Equal(byRemote.Split('\n')[0], byRoot.Split('\n')[0]);

        var get = await ReadServer.CallAsync(client, "work_get", new Dictionary<string, object?> { ["refs"] = new[] { "other" } }, Ct);
        Assert.Contains("  repo      remote github.com/r-larch/other", get, StringComparison.Ordinal);
        Assert.Contains("  repo      root   p:/projects/larchsys/other", get, StringComparison.Ordinal);

        var tree = await ReadServer.CallAsync(client, "work_tree", new Dictionary<string, object?> { ["scope"] = "other" }, Ct);
        Assert.DoesNotContain("github.com", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_brief_for_an_unbound_repo_is_onboarding_that_names_the_call_with_the_same_repo()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var text = await ReadServer.CallAsync(client, "work_brief", Repo(Remote, Root), Ct);

        Assert.StartsWith("RUN —  ·  this repo is not bound to a project", text, StringComparison.Ordinal);
        const string repo = "repo:{remote:\"github.com/r-larch/other\", root:\"p:/projects/larchsys/other\"}";
        Assert.Contains($"work_create(items:[{{kind:\"project\", ref:\"<slug>\", title:\"<what the repo is>\", {repo}}}])", text, StringComparison.Ordinal);
        Assert.Contains($"work_update(ref:\"<project>\", {repo})", text, StringComparison.Ordinal);
        Assert.Contains(await ProjectRefAsync(world), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_brief_with_no_repo_keeps_the_busiest_run_and_says_how_to_pass_one()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var lines = (await ReadServer.CallAsync(client, "work_brief", null, Ct)).Split('\n');
        Assert.Contains("run-a", lines[0], StringComparison.Ordinal);
        Assert.Single(lines, l => l.StartsWith("repo     not given — work_brief(repo:{remote, root})", StringComparison.Ordinal));

        var scoped = await ReadServer.CallAsync(client, "work_brief", new Dictionary<string, object?> { ["scope"] = "run-a" }, Ct);
        Assert.DoesNotContain("repo     not given", scoped, StringComparison.Ordinal);
    }

    /// <summary>
    /// The launcher sends the keys it computed in the repo it launched into; a worktree and the main
    /// checkout send the same root, so both sessions land on the same project with no arguments.
    /// </summary>
    [Fact]
    public async Task A_session_minted_with_its_repo_opens_that_project_from_a_bare_work_brief()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateOtherAsync(world);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var fromMain = await MintAsync(server, "aw-main", new { root = "P:/Projects/LarchSys/Other" });
        var fromWorktree = await MintAsync(server, "aw-wt", new { remote = "https://github.com/r-Larch/Other.git", root = @"P:\Projects\LarchSys\Other\" });

        await using (var db = fixture.NewContext())
        {
            var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId && s.Name == "aw-wt", Ct);
            Assert.Equal("github.com/r-larch/other", row.RepoRemote);
            Assert.Equal("p:/projects/larchsys/other", row.RepoRoot);
        }

        foreach (var token in new[] { fromMain, fromWorktree })
        {
            await using var client = await server.ConnectAsync(token, Ct);
            var brief = await ReadServer.CallAsync(client, "work_brief", null, Ct);
            Assert.StartsWith("PROJECT other  ·  The other repo", brief.Split('\n')[0], StringComparison.Ordinal);
            Assert.Contains("\nNEXT   resume run other-run — work_brief(scope:\"other-run\")", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("repo     not given", brief, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_v1_overview_carries_each_projects_repos()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var world = v1.World;
        var project = await ProjectRefAsync(world);
        await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg(Remote, Root)), Ct);
        await using var server = await v1.StartAsync(fixture);

        var overview = await V1Http.GetAsync(V1Http.Reader(server), "/api/v1/overview");

        var repos = V1Http.Array(Assert.Single(V1Http.Array(overview, "projects")), "repos");
        Assert.Equal(
            ["remote github.com/r-larch/other", "root p:/projects/larchsys/other"],
            repos.Select(r => $"{r.GetProperty("kind").GetString()} {r.GetProperty("key").GetString()}"));
    }

    [Fact]
    public async Task The_repos_migration_goes_down_and_up_again()
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
            var session = Guid.CreateVersion7();

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                    INSERT INTO item (id, workspace_id, kind, ref, seq, title) VALUES (@project, @workspace, 'project', 'p', 1, 'P');
                    INSERT INTO project_repo (id, workspace_id, project_item_id, kind, key)
                    VALUES (gen_random_uuid(), @workspace, @project, 'root', 'p:/x');
                    INSERT INTO agent_session (id, workspace_id, name, role, repo_root) VALUES (@session, @workspace, 'aw-m17', 'worker', 'p:/x');
                    """,
                    new { workspace, slug = $"mig-{workspace:n}", project, session });

                var duplicate = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                    """
                    INSERT INTO project_repo (id, workspace_id, project_item_id, kind, key)
                    VALUES (gen_random_uuid(), @workspace, @project, 'root', 'p:/x');
                    """,
                    new { workspace, project }));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.GetService<IMigrator>().MigrateAsync(BeforeRepos, Ct);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                Assert.Null(await connection.ExecuteScalarAsync<string>("SELECT to_regclass('project_repo')::text;"));
                Assert.Empty(await connection.QueryAsync<string>(
                    """
                    SELECT column_name FROM information_schema.columns
                    WHERE table_name = 'agent_session' AND column_name IN ('repo_remote', 'repo_root');
                    """));
                Assert.Equal("aw-m17", await connection.ExecuteScalarAsync<string>(
                    "SELECT name FROM agent_session WHERE id = @session;", new { session }));
            }

            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
                Assert.Empty(await context.ProjectRepos.ToListAsync(Ct));
                Assert.Null((await context.AgentSessions.SingleAsync(s => s.Id == session, Ct)).RepoRoot);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    /// <summary>A second project, bound to <see cref="Remote"/> and <see cref="Root"/>, with an idle run.</summary>
    private static async Task CreateOtherAsync(WriteWorld world)
    {
        var project = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "The other repo", Ref: "other", Repo: new RepoArg(Remote, Root))], Ct);
        Assert.False(project.Refused, project.Text);

        var run = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("run", "The other run", Ref: "other-run", Parent: "other")], Ct);
        Assert.False(run.Refused, run.Text);
    }

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private async Task<List<string>> KeysAsync(WriteWorld world, string projectRef)
    {
        await using var db = fixture.NewContext();
        var project = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == projectRef, Ct);
        return [.. (await db.ProjectRepos.Where(r => r.ProjectItemId == project.Id).ToListAsync(Ct))
            .Select(r => $"{r.Kind} {r.Key}")
            .Order()];
    }

    private static Dictionary<string, object?> Repo(string? remote = null, string? root = null)
        => new()
        {
            ["repo"] = new Dictionary<string, object?> { ["remote"] = remote, ["root"] = root },
        };

    private static async Task<string> MintAsync(AppServer server, string name, object repo)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadServer.ControllerToken);

        var response = await client.PostAsync("/api/agent-sessions", AppServer.Json(new { name, role = "orchestrator", repo }), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }
}

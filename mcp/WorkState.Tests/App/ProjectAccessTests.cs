using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using ModelContextProtocol.Protocol;
using WorkState.Api.App;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// C66/C67, the matrix (doc 02 §15b): within one tenant, a user who is not a member of a project can read, search,
/// stream, resolve and write nothing of it, over every credential kind they hold; a member of both, and a static
/// token, see both. The two projects share every ref below them, so a leak is the other project's title, never a
/// miss that happened to look right; and a non-member is answered as for a ref or an id that does not exist.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectAccessTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ProjectAccess_a_non_member_reads_nothing_of_the_other_project_over_mcp_and_its_resources()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        foreach (var credential in (await world.CredentialsAsync(world.User1)).Where(c => c.Mcp is not null))
        {
            await using var mcp = await world.McpAsync(credential.Mcp!);

            // The shared ref is the caller's own project's, and not ambiguous: the other one is not there to share it.
            var own = await ReadServer.CallEitherAsync(mcp, "work_get", Args(("refs", new[] { "run-a/1.1" })), Ct);
            Assert.False(own.Refused, $"{credential}: {own.Text}");
            Assert.Contains(ProjectWorld.TitleP1, own.Text, StringComparison.Ordinal);

            foreach (var (tool, args) in Reads())
            {
                var read = await ReadServer.CallEitherAsync(mcp, tool, args, Ct);
                AssertNothingOfP2(world, $"{credential} {tool}({string.Join(", ", args.Keys)})", read.Text);
            }

            foreach (var uri in new[]
                     {
                         WorkUris.Workspace, WorkUris.WorkspaceBrief, WorkUris.Handoff, WorkUris.Attention,
                         $"work://item/{ProjectWorld.TaskP2Path}", $"work://item/{ProjectWorld.TaskP2Path}/brief",
                         $"work://item/{ProjectWorld.P2}", WorkUris.AttentionFor($"{ProjectWorld.P2}/run-a"),
                     })
            {
                AssertNothingOfP2(world, $"{credential} {uri}", await ResourceAsync(mcp, uri));
            }
        }

        foreach (var credential in new[] { (await world.CredentialsAsync(world.User2))[1], world.Static })
        {
            await using var mcp = await world.McpAsync(credential.Mcp!);
            var theirs = await ReadServer.CallEitherAsync(mcp, "work_get", Args(("refs", new[] { ProjectWorld.TaskP2Path })), Ct);
            Assert.Contains(ProjectWorld.TitleP2, theirs.Text, StringComparison.Ordinal);

            var found = await ReadServer.CallEitherAsync(mcp, "work_search", Args(("q", ProjectWorld.Marker)), Ct);
            Assert.Contains(ProjectWorld.TitleOnlyInP2, found.Text, StringComparison.Ordinal);

            var history = await ReadServer.CallEitherAsync(mcp, "work_history", Args(), Ct);
            Assert.Contains(ProjectWorld.NoteP2, history.Text, StringComparison.Ordinal);

            var bound = await ReadServer.CallEitherAsync(mcp, "work_brief", Args(("repo", new { remote = ProjectWorld.RemoteP2 })), Ct);
            Assert.Contains(ProjectWorld.P2, bound.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProjectAccess_a_non_member_reads_nothing_of_the_other_project_over_v1_links_and_the_dashboard()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        foreach (var credential in await world.CredentialsAsync(world.User1))
        {
            var http = credential.Http;
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/v1/items/{world.One.TaskId}", Ct)).StatusCode);

            var unknown = Guid.CreateVersion7();
            var missing = await (await http.GetAsync($"/api/v1/items/{unknown}", Ct)).Content.ReadAsStringAsync(Ct);
            foreach (var id in world.P2Ids)
            {
                var response = await http.GetAsync($"/api/v1/items/{id}", Ct);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{credential} item {id} → {(int)response.StatusCode}");
                Assert.Equal(missing, (await response.Content.ReadAsStringAsync(Ct)).Replace(id.ToString(), unknown.ToString()));
            }

            foreach (var path in new[]
                     {
                         $"/api/v1/items/{world.P2TaskId}/brief",
                         $"/api/v1/runs/{world.P2RunId}/board",
                         $"/api/v1/backlog?project={world.P2Id}",
                         $"/api/v1/prompts?scope={world.P2RunId}",
                         $"/api/v1/journal?scope={world.P2RunId}",
                     })
            {
                var response = await http.GetAsync(path, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{credential} {path} → {(int)response.StatusCode}");
            }

            Assert.Equal([world.P1Id], ProjectIds(await JsonAsync(http, "/api/v1/overview")));

            var hits = (await JsonAsync(http, $"/api/v1/search?q={ProjectWorld.Marker}")).GetProperty("hits").EnumerateArray()
                .Select(h => h.GetProperty("item").GetProperty("id").GetGuid()).ToList();
            Assert.Contains(world.One.TaskId, hits);
            Assert.Empty(hits.Intersect(world.P2Ids));

            Assert.Equal([world.One.TaskId], Ids(await JsonAsync(http, "/api/v1/resolve?ref=run-a/1.1")));
            Assert.Empty(Ids(await JsonAsync(http, $"/api/v1/resolve?ref={ProjectWorld.TaskP2Path}")));
            Assert.Empty(Ids(await JsonAsync(http, $"/api/v1/resolve?ref={ProjectWorld.OnlyInP2}")));
            Assert.Empty(Ids(await JsonAsync(http, $"/api/v1/resolve?ref={ProjectWorld.P2}")));

            foreach (var path in new[] { "/api/v1/journal", "/api/v1/inbox", "/api/v1/sessions", $"/api/v1/search?q={ProjectWorld.Marker}" })
            {
                AssertNothingOfP2(world, $"{credential} {path}", (await JsonAsync(http, path)).ToString());
            }

            if (credential.Mcp is null)
            {
                await AssertCookieSeesNothingOfP2Async(world, credential);
            }
        }

        var u2 = (await world.CredentialsAsync(world.User2))[0];
        foreach (var credential in new[] { u2, world.Static })
        {
            Assert.Equal(HttpStatusCode.OK, (await credential.Http.GetAsync($"/api/v1/items/{world.P2TaskId}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await credential.Http.GetAsync($"/api/v1/runs/{world.P2RunId}/board", Ct)).StatusCode);
            Assert.Equal(
                new[] { world.P1Id, world.P2Id }.Order(),
                ProjectIds(await JsonAsync(credential.Http, "/api/v1/overview")).Order());
            Assert.Equal([world.P2TaskId], Ids(await JsonAsync(credential.Http, $"/api/v1/resolve?ref={ProjectWorld.TaskP2Path}")));
        }

        var link = await u2.Http.GetAsync($"/r/{ProjectWorld.TaskP2Path}", Ct);
        Assert.Equal(HttpStatusCode.Redirect, link.StatusCode);
        Assert.Equal($"/runs/{world.P2RunId}/{world.P2TaskId}", link.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.MultipleChoices, (await u2.Http.GetAsync("/r/run-a/1.1", Ct)).StatusCode);
    }

    /// <summary>The cookie's own surfaces: <c>/r/</c> links, and the dashboard's project writes and member list.</summary>
    private static async Task AssertCookieSeesNothingOfP2Async(ProjectWorld world, Credential cookie)
    {
        var own = await cookie.Http.GetAsync("/r/run-a/1.1", Ct);
        Assert.Equal(HttpStatusCode.Redirect, own.StatusCode);
        Assert.Equal($"/runs/{world.One.RunId}/{world.One.TaskId}", own.Headers.Location!.OriginalString);

        foreach (var path in new[] { ProjectWorld.TaskP2Path, ProjectWorld.OnlyInP2Path, ProjectWorld.OnlyInP2, ProjectWorld.P2 })
        {
            var response = await cookie.Http.GetAsync($"/r/{path}", Ct);
            var page = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"/r/{path} → {(int)response.StatusCode}");
            Assert.Contains("Nothing has the path", page, StringComparison.Ordinal);
            AssertNothingOfP2(world, $"/r/{path}", page);
        }

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, $"/api/projects/{world.P2Id}/archive"),
                     (HttpMethod.Get, $"/api/projects/{world.P2Id}/members"),
                     (HttpMethod.Post, $"/api/projects/{world.P2Id}/members"),
                 })
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
            if (method == HttpMethod.Post && path.EndsWith("/members", StringComparison.Ordinal))
            {
                request.Content = AppServer.Json(new { all = true });
            }

            var response = await cookie.Http.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} → {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task ProjectAccess_a_non_member_cannot_write_into_the_other_project()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var before = await world.P2CountsAsync();

        foreach (var credential in (await world.CredentialsAsync(world.User1)).Where(c => c.Mcp is not null))
        {
            await using var mcp = await world.McpAsync(credential.Mcp!);
            foreach (var target in new[] { ProjectWorld.TaskP2Path, ProjectWorld.OnlyInP2, ProjectWorld.OnlyInP2Path })
            {
                foreach (var (tool, args) in new (string, IReadOnlyDictionary<string, object?>)[]
                         {
                             ("work_status", Args(("ref", target), ("to", "in_progress"))),
                             ("work_update", Args(("ref", target), ("title", "taken over"))),
                             ("work_note", Args(("kind", "observation"), ("body", "leak"), ("to", target))),
                             ("work_verify", Args(("ref", target), ("command", "dotnet test"), ("result_line", "Passed! Failed: 0, Passed: 1"))),
                             ("work_link", Args(("from", "run-a/1.1"), ("to", target), ("type", "blocks"))),
                             ("work_move", Args(("ref", "run-a/1.1"), ("parent", target))),
                             ("work_assume", Args(("ref", target), ("statement", "leak"), ("evidence", "asserted"))),
                             ("work_constrain", Args(("kind", "stack_fact"), ("text", "leak"), ("scope", target))),
                             ("work_finding", Args(("findings", new[] { new NewFinding(target, "leak", "minor", "review") }))),
                             ("work_create", Args(("items", new[]
                             {
                                 new NewItemArg("task", "leak", Parent: $"{ProjectWorld.P2}/run-a/1", VerifyCmd: "dotnet test", Criteria: ["x"]),
                             }))),
                         })
                {
                    var written = await ReadServer.CallEitherAsync(mcp, tool, args, Ct);
                    Assert.True(written.Refused, $"{credential} {tool} → {target}: {written.Text}");
                    AssertNothingOfP2(world, $"{credential} {tool} → {target}", written.Text);
                }
            }
        }

        Assert.Equal(before, await world.P2CountsAsync());
        Assert.Equal(ItemStatus.Open, await world.StatusAsync(world.P2TaskId));
        Assert.Equal(ItemStatus.Open, await world.StatusAsync(world.OnlyInP2Id));
    }

    [Fact]
    public async Task ProjectAccess_an_event_of_the_other_project_never_reaches_a_non_members_stream()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        foreach (var credential in await world.CredentialsAsync(world.User1))
        {
            var (seen, own) = await StreamAsync(world, credential, world.One.TaskId, ProjectWorld.TaskP2Path, $"{ProjectWorld.P1}/run-a/1.1");
            Assert.True(own, $"{credential} never received its own project's event");
            Assert.Empty(seen.Intersect(world.P2Ids));
        }

        foreach (var credential in new[] { (await world.CredentialsAsync(world.User2))[0], world.Static })
        {
            var (seen, own) = await StreamAsync(world, credential, world.P2TaskId, "pa-one/run-a/1.1", ProjectWorld.TaskP2Path);
            Assert.True(own, $"{credential} never received P2's event");
            Assert.Contains(world.P2TaskId, seen);
        }
    }

    [Fact]
    public async Task ProjectAccess_brief_next_and_the_overview_list_only_the_callers_projects_and_a_default_scope_stays_in_them()
    {
        // P2's run has the most open work, so a default scope that could see it would land there.
        var world = await ProjectWorld.CreateAsync(fixture, extraOpenInP2: 6);
        await using var server = world.Server;
        var u1 = await world.CredentialsAsync(world.User1);
        var u2 = await world.CredentialsAsync(world.User2);

        foreach (var credential in u1.Where(c => c.Mcp is not null))
        {
            await using var mcp = await world.McpAsync(credential.Mcp!);
            foreach (var tool in new[] { "work_brief", "work_next", "work_tree", "work_threads" })
            {
                var read = await ReadServer.CallEitherAsync(mcp, tool, Args(), Ct);
                Assert.False(read.Refused, $"{credential} {tool}: {read.Text}");
                AssertNothingOfP2(world, $"{credential} {tool}()", read.Text);
                Assert.DoesNotContain("quokka backlog of P2", read.Text, StringComparison.Ordinal);
            }

            var next = await ReadServer.CallEitherAsync(mcp, "work_next", Args(), Ct);
            Assert.Contains(ProjectWorld.TitleP1, next.Text, StringComparison.Ordinal);
        }

        foreach (var credential in new[] { u2[1], world.Static })
        {
            await using var mcp = await world.McpAsync(credential.Mcp!);
            var next = await ReadServer.CallEitherAsync(mcp, "work_next", Args(), Ct);
            Assert.Contains("quokka backlog of P2", next.Text, StringComparison.Ordinal);
        }

        Assert.Equal([world.P1Id], ProjectIds(await JsonAsync(u1[0].Http, "/api/v1/overview")));
        Assert.Equal(new[] { world.P1Id, world.P2Id }.Order(), ProjectIds(await JsonAsync(u2[0].Http, "/api/v1/overview")).Order());
        Assert.Equal(new[] { world.P1Id, world.P2Id }.Order(), ProjectIds(await JsonAsync(world.Static.Http, "/api/v1/overview")).Order());

        // The tenant's admin manages members without being one, and reads no project's work.
        Assert.Empty(ProjectIds(await JsonAsync(await server.SignInAsync(Ct), "/api/v1/overview")));
        var inbox = (await JsonAsync(u1[0].Http, "/api/v1/inbox")).ToString();
        AssertNothingOfP2(world, "inbox", inbox);
    }

    [Fact]
    public async Task ProjectAccess_a_member_has_full_read_and_write_in_their_project()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var oauth = (await world.CredentialsAsync(world.User1))[1];
        await using var mcp = await world.McpAsync(oauth.Mcp!);

        await OkAsync(mcp, "work_create", Args(("items", new[]
        {
            new NewItemArg("task", "quokka written by a member", Ref: "1.5", Parent: $"{ProjectWorld.P1}/run-a/1", VerifyCmd: "dotnet test --filter Member", Criteria: ["it lands"]),
        })));
        await OkAsync(mcp, "work_update", Args(("ref", "run-a/1.5"), ("intent", "a member edits their own project")));
        await OkAsync(mcp, "work_link", Args(("from", "run-a/1.1"), ("to", "run-a/1.5"), ("type", "blocks")));
        await OkAsync(mcp, "work_status", Args(("ref", "run-a/1.1"), ("to", "in_progress"), ("tree", $"pa-tree-{Guid.CreateVersion7():n}")));
        await OkAsync(mcp, "work_verify", Args(("ref", "run-a/1.1"), ("command", "dotnet test --filter Guard"), ("result_line", "Passed! Failed: 0, Passed: 3")));
        await OkAsync(mcp, "work_note", Args(("kind", "observation"), ("body", "a member's note"), ("to", "run-a/1.5")));
        await OkAsync(mcp, "work_assume", Args(("ref", $"{ProjectWorld.P1}/run-a"), ("statement", "a member's assumption"), ("evidence", "asserted"), ("reversal", "low")));
        await OkAsync(mcp, "work_finding", Args(("findings", new[] { new NewFinding("run-a/1.1", "a member's finding", "minor", "review") })));
        await OkAsync(mcp, "work_constrain", Args(("kind", "stack_fact"), ("text", "a member's rule"), ("scope", ProjectWorld.P1)));
        await OkAsync(mcp, "work_move", Args(("ref", "run-a/1.5"), ("parent", $"{ProjectWorld.P1}/run-a/2")));

        var tree = await OkAsync(mcp, "work_tree", Args(("scope", $"{ProjectWorld.P1}/run-a")));
        Assert.Contains("quokka written by a member", tree, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync(world.One.TaskId));

        var brief = await oauth.Http.GetAsync($"/api/v1/items/{world.One.TaskId}/brief", Ct);
        Assert.Equal(HttpStatusCode.OK, brief.StatusCode);
        Assert.Contains("a member's rule", await brief.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAccess_adding_a_member_shows_the_project_on_the_next_request_and_the_open_stream_and_removing_hides_it()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CredentialsAsync(world.User1);
        var u2 = await world.CredentialsAsync(world.User2);
        var members = $"/api/projects/{world.P2Id}/members";

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(2));
        using var stream = await u1[0].Http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(session.Token));
        var frames = Channel.CreateUnbounded<JsonElement>();
        _ = PumpAsync(reader, frames, session.Token);

        // Not a member: P2's event is not sent, P1's after it is.
        Assert.Empty((await DrainUntilAsync(world, frames, ProjectWorld.TaskP2Path, $"{ProjectWorld.P1}/run-a/1.1", world.One.TaskId, session.Token)).Intersect(world.P2Ids));

        await AdminApi.JsonAsync(await AdminApi.SendAsync(u2[0].Http, HttpMethod.Post, members, new { userId = world.User1Id }), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, (await u1[0].Http.GetAsync($"/api/v1/items/{world.P2TaskId}", Ct)).StatusCode);
        Assert.Contains(world.P2Id, ProjectIds(await JsonAsync(u1[1].Http, "/api/v1/overview")));
        await using (var mcp = await world.McpAsync(u1[1].Mcp!))
        {
            var read = await ReadServer.CallEitherAsync(mcp, "work_get", Args(("refs", new[] { ProjectWorld.TaskP2Path })), Ct);
            Assert.Contains(ProjectWorld.TitleP2, read.Text, StringComparison.Ordinal);
        }

        // The stream opened before the change: P2's next event now reaches it.
        Assert.Contains(world.P2TaskId, await DrainUntilAsync(world, frames, $"{ProjectWorld.P1}/run-a/1.1", ProjectWorld.TaskP2Path, world.P2TaskId, session.Token));

        Assert.Equal(HttpStatusCode.NoContent, (await AdminApi.SendAsync(u2[0].Http, HttpMethod.Delete, $"{members}/{world.User1Id}")).StatusCode);
        var removedAt = await MemberRemovedSeqAsync(world);

        var gone = await u1[0].Http.GetAsync($"/api/v1/items/{world.P2TaskId}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal([world.P1Id], ProjectIds(await JsonAsync(u1[1].Http, "/api/v1/overview")));

        var after = await DrainUntilAsync(world, frames, ProjectWorld.TaskP2Path, "pa-one/run-a/1.1", world.One.TaskId, session.Token, removedAt);
        Assert.Empty(after.Intersect(world.P2Ids));
        await session.CancelAsync();
    }

    [Fact]
    public async Task ProjectAccess_a_numbered_project_skips_a_number_a_hidden_project_holds_and_a_bound_repo_is_refused_without_its_project()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE item SET ref = 'project-1' WHERE id = @p2;", new { p2 = world.P2Id });
        }

        var oauth = (await world.CredentialsAsync(world.User1))[1];
        await using var mcp = await world.McpAsync(oauth.Mcp!);

        var numbered = await OkAsync(mcp, "work_create", Args(("items", new[] { new NewItemArg("project", "numbered by the server") })));
        Assert.Contains("project-2", numbered, StringComparison.Ordinal);

        var bound = await ReadServer.CallEitherAsync(mcp, "work_create", Args(("items", new[]
        {
            new NewItemArg("project", "wants P2's repository", Ref: "pa-three", Repo: new RepoArg(Remote: ProjectWorld.RemoteP2)),
        })), Ct);
        Assert.True(bound.Refused, bound.Text);
        Assert.Contains("not a member of", bound.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("project-1", bound.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(bound.Text);
    }

    /// <summary>A user who is not in the workspace could not be a member, so their project would be hidden from them.</summary>
    [Fact]
    public async Task ProjectAccess_a_user_who_is_not_in_the_workspace_cannot_create_a_project_there()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var outsider = Guid.CreateVersion7();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                "INSERT INTO app_user (id, name, password_hash) VALUES (@outsider, @name, 'x');",
                new { outsider, name = $"pa-out-{outsider:n}"[..20] });
        }

        var refused = await world.One.Writer.CreateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator with { UserId = outsider }, [new NewItemArg("project", "an outsider's", Ref: "pa-out")], Ct);
        Assert.True(refused.Refused, refused.Text);
        Assert.Contains("not a member of this workspace", refused.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(refused.Text);

        var created = await world.One.Writer.CreateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator with { UserId = world.User1Id }, [new NewItemArg("project", "a member's", Ref: "pa-in")], Ct);
        Assert.False(created.Refused, created.Text);
    }

    /// <summary>MCP reads a non-member makes; each must show nothing of P2.</summary>
    private static (string Tool, IReadOnlyDictionary<string, object?> Args)[] Reads()
        =>
        [
            ("work_get", Args(("refs", new[] { ProjectWorld.TaskP2Path }))),
            ("work_get", Args(("refs", new[] { ProjectWorld.OnlyInP2 }))),
            ("work_get", Args(("refs", new[] { ProjectWorld.OnlyInP2Path }))),
            ("work_get", Args(("refs", new[] { ProjectWorld.P2 }))),
            ("work_brief", Args()),
            ("work_brief", Args(("scope", ProjectWorld.P2))),
            ("work_brief", Args(("scope", $"{ProjectWorld.P2}/run-a"))),
            ("work_brief", Args(("repo", new { remote = ProjectWorld.RemoteP2 }))),
            ("work_next", Args()),
            ("work_next", Args(("scope", $"{ProjectWorld.P2}/run-a"))),
            ("work_tree", Args()),
            ("work_tree", Args(("scope", ProjectWorld.P2))),
            ("work_search", Args(("q", ProjectWorld.Marker))),
            ("work_search", Args(("q", ProjectWorld.Marker), ("scope", ProjectWorld.P2))),
            ("work_history", Args()),
            ("work_history", Args(("scope", $"{ProjectWorld.P2}/run-a"))),
            ("work_threads", Args()),
            ("work_audit", Args()),
        ];

    /// <summary>P2's titles, its note and its ids. Not its refs or its remote: a refusal echoes the ref it was asked for.</summary>
    private static void AssertNothingOfP2(ProjectWorld world, string what, string text)
    {
        foreach (var leak in new[] { ProjectWorld.TitleP2, ProjectWorld.TitleOnlyInP2, ProjectWorld.NoteP2, "Foundations of P2", "The other project's run" })
        {
            Assert.False(text.Contains(leak, StringComparison.OrdinalIgnoreCase), $"{what} shows {leak}: {text}");
        }

        foreach (var id in world.P2Ids)
        {
            Assert.False(text.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase), $"{what} shows P2's id {id}: {text}");
        }
    }

    /// <summary>
    /// Opens the stream, writes an event on <paramref name="other"/> and then on <paramref name="own"/> until the latter
    /// arrives — notifications arrive in commit order, so once it is here the first would have been.
    /// </summary>
    private static async Task<(HashSet<Guid> Seen, bool Own)> StreamAsync(
        ProjectWorld world, Credential credential, Guid ownId, string other, string own)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(1));
        using var response = await credential.Http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(session.Token));
        var frames = Channel.CreateUnbounded<JsonElement>();
        _ = PumpAsync(reader, frames, session.Token);

        var seen = await DrainUntilAsync(world, frames, other, own, ownId, session.Token);
        await session.CancelAsync();
        return (seen, seen.Contains(ownId));
    }

    /// <summary>The item ids of the frames that arrived, from a note on <paramref name="first"/> to the one on <paramref name="then"/>.</summary>
    private static async Task<HashSet<Guid>> DrainUntilAsync(
        ProjectWorld world, Channel<JsonElement> frames, string first, string then, Guid until, CancellationToken cancellationToken, long afterSeq = 0)
    {
        var seen = new HashSet<Guid>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await world.NoteAsync(first, $"stream probe {attempt}");
            await world.NoteAsync(then, $"stream probe {attempt}");
            while (await NextAsync(frames, TimeSpan.FromSeconds(4), cancellationToken) is { } frame)
            {
                if (frame.GetProperty("seq").GetInt64() <= afterSeq)
                {
                    continue;
                }

                if (frame.TryGetProperty("itemId", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    seen.Add(id.GetGuid());
                }

                if (seen.Contains(until))
                {
                    return seen;
                }
            }
        }

        return seen;
    }

    private async Task<long> MemberRemovedSeqAsync(ProjectWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT max(seq) FROM event WHERE item_id = @p2 AND kind = 'member_removed';", new { p2 = world.P2Id });
    }

    private static async Task<string> OkAsync(ModelContextProtocol.Client.McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
    {
        var (text, refused) = await ReadServer.CallEitherAsync(mcp, tool, args, Ct);
        Assert.False(refused, $"{tool} was refused:\n{text}");
        return text;
    }

    private static async Task<string> ResourceAsync(ModelContextProtocol.Client.McpClient client, string uri)
    {
        try
        {
            var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
            return string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
        }
        catch (ModelContextProtocol.McpException refused)
        {
            return refused.Message;
        }
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);

    private static List<Guid> Ids(JsonElement resolved)
        => [.. resolved.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("id").GetGuid())];

    private static List<Guid> ProjectIds(JsonElement overview)
        => [.. overview.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("item").GetProperty("id").GetGuid())];

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} → {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task PumpAsync(StreamReader reader, Channel<JsonElement> frames, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    await frames.Writer.WriteAsync(JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone(), cancellationToken);
                }
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException)
        {
        }
        finally
        {
            frames.Writer.TryComplete();
        }
    }

    private static async Task<JsonElement?> NextAsync(Channel<JsonElement> frames, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var within = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        within.CancelAfter(wait);

        try
        {
            return await frames.Reader.ReadAsync(within.Token);
        }
        catch (Exception failure) when (failure is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }
}

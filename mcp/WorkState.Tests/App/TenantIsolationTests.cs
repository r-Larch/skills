using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using WorkState.Api.App;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// C62/C63, the matrix: for every credential kind acting in tenant A — the dashboard cookie, an OAuth token, a
/// <c>wss_</c> session token, a static token bound to A — nothing of tenant B can be read, written, streamed,
/// resolved or minted into. Both tenants hold the same refs, so a leak is the other tenant's row, never a 404
/// that happened to look right.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TenantIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Tenant_A_callers_read_A_and_never_B_over_mcp()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;

        foreach (var credential in (await world.CredentialsOfAAsync()).Where(c => c.Mcp is not null))
        {
            await using var mcp = await world.McpAsync(credential.Mcp!);

            var own = await ReadServer.CallEitherAsync(mcp, "work_get", Args(("refs", new[] { "run-a/1.1" })), Ct);
            Assert.Contains(TenantWorld.TitleA, own.Text, StringComparison.Ordinal);

            foreach (var (tool, args) in new (string, IReadOnlyDictionary<string, object?>)[]
                     {
                         ("work_get", Args(("refs", new[] { TenantWorld.OnlyInB }))),
                         ("work_get", Args(("refs", new[] { TenantWorld.OnlyInBPath }))),
                         ("work_get", Args(("refs", new[] { $"{TenantWorld.Project}/run-a/1.1" }))),
                         ("work_brief", Args(("scope", TenantWorld.OnlyInBPath))),
                         ("work_brief", Args(("scope", TenantWorld.Project))),
                         ("work_brief", Args(("scope", "run-a"))),
                         ("work_brief", Args()),
                         ("work_search", Args(("q", TenantWorld.Marker))),
                         ("work_tree", Args()),
                     })
            {
                var read = await ReadServer.CallEitherAsync(mcp, tool, args, Ct);
                Assert.False(read.Text.Contains(TenantWorld.TitleB, StringComparison.Ordinal), $"{credential} {tool} read B: {read.Text}");
                Assert.False(read.Text.Contains(TenantWorld.TitleOnlyInB, StringComparison.Ordinal), $"{credential} {tool} read B: {read.Text}");
            }
        }
    }

    [Fact]
    public async Task Tenant_A_callers_read_A_and_never_B_over_v1()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;

        foreach (var credential in await world.CredentialsOfAAsync())
        {
            var http = credential.Http;

            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/v1/items/{world.A.TaskId}", Ct)).StatusCode);
            foreach (var path in new[]
                     {
                         $"/api/v1/items/{world.B.TaskId}",
                         $"/api/v1/items/{world.OnlyInBId}",
                         $"/api/v1/items/{world.B.TaskId}/brief",
                         $"/api/v1/runs/{world.B.RunId}/board",
                         $"/api/v1/backlog?project={world.B.ProjectId}",
                         $"/api/v1/prompts?scope={world.B.RunId}",
                         $"/api/v1/journal?scope={world.B.RunId}",
                     })
            {
                var response = await http.GetAsync(path, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{credential} {path} → {(int)response.StatusCode}");
            }

            var overview = await JsonAsync(http, "/api/v1/overview");
            var projects = overview.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("item").GetProperty("id").GetGuid()).ToList();
            Assert.Contains(world.A.ProjectId, projects);
            Assert.DoesNotContain(world.B.ProjectId, projects);

            var hits = (await JsonAsync(http, $"/api/v1/search?q={TenantWorld.Marker}")).GetProperty("hits").EnumerateArray()
                .Select(h => h.GetProperty("item").GetProperty("id").GetGuid()).ToList();
            Assert.Contains(world.A.TaskId, hits);
            Assert.DoesNotContain(world.B.TaskId, hits);
            Assert.DoesNotContain(world.OnlyInBId, hits);

            Assert.Equal([world.A.TaskId], Ids(await JsonAsync(http, $"/api/v1/resolve?ref={TenantWorld.Project}/run-a/1.1")));
            Assert.Empty(Ids(await JsonAsync(http, $"/api/v1/resolve?ref={TenantWorld.OnlyInB}")));
            Assert.Empty(Ids(await JsonAsync(http, $"/api/v1/resolve?ref={TenantWorld.OnlyInBPath}")));

            var inbox = (await JsonAsync(http, "/api/v1/inbox")).ToString();
            Assert.DoesNotContain(world.B.WorkspaceId.ToString(), inbox, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.B.TaskId.ToString(), inbox, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Tenant_A_callers_cannot_write_in_tenant_B()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var itemsInA = await world.ItemCountAsync(world.A.WorkspaceId);
        var itemsInB = await world.ItemCountAsync(world.B.WorkspaceId);

        foreach (var credential in await world.CredentialsOfAAsync())
        {
            if (credential.Mcp is { } token)
            {
                await using var mcp = await world.McpAsync(token);

                foreach (var onlyInB in new[] { TenantWorld.OnlyInB, TenantWorld.OnlyInBPath })
                {
                    var status = await ReadServer.CallEitherAsync(mcp, "work_status", Args(("ref", onlyInB), ("to", "in_progress")), Ct);
                    Assert.True(status.Refused, $"{credential} work_status: {status.Text}");

                    var note = await ReadServer.CallEitherAsync(
                        mcp, "work_note", Args(("kind", "observation"), ("body", "tenant leak"), ("to", onlyInB)), Ct);
                    Assert.True(note.Refused, $"{credential} work_note: {note.Text}");
                }

                // The same refs exist in both tenants: a write that names them lands in the caller's own.
                var created = await ReadServer.CallEitherAsync(mcp, "work_create", Args(("items", new[]
                {
                    new NewItemArg("task", $"tenant own {credential}", Parent: $"{TenantWorld.Project}/run-a/1", VerifyCmd: "dotnet test", Criteria: ["it lands in A"]),
                })), Ct);
                Assert.False(created.Refused, $"{credential} work_create: {created.Text}");

                var own = await ReadServer.CallEitherAsync(
                    mcp, "work_note", Args(("kind", "observation"), ("body", $"tenant own {credential}"), ("to", $"{TenantWorld.Project}/run-a/1.1")), Ct);
                Assert.False(own.Refused, $"{credential} own work_note: {own.Text}");
            }
            else
            {
                // The cookie's one write: archiving. B's project is as unknown as a typo.
                var archive = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{world.B.ProjectId}/archive");
                archive.Headers.Add(ProjectEndpoints.WriteHeader, "1");
                Assert.Equal(HttpStatusCode.NotFound, (await credential.Http.SendAsync(archive, Ct)).StatusCode);
            }
        }

        Assert.Equal(itemsInB, await world.ItemCountAsync(world.B.WorkspaceId));
        Assert.Equal(itemsInA + 3, await world.ItemCountAsync(world.A.WorkspaceId));
        Assert.Equal(ItemStatus.Open, await world.StatusAsync(world.B.TaskId));
        Assert.Equal(ItemStatus.Open, await world.StatusAsync(world.OnlyInBId));

        await using var db = fixture.NewContext();
        Assert.False(await db.Notes.AnyAsync(n => n.WorkspaceId == world.B.WorkspaceId, Ct));
        Assert.Equal(3, await db.Notes.CountAsync(n => n.WorkspaceId == world.A.WorkspaceId && n.Body.StartsWith("tenant own"), Ct));
        Assert.Null((await db.Items.SingleAsync(i => i.Id == world.B.ProjectId, Ct)).ArchivedAt);
    }

    [Fact]
    public async Task A_tenant_B_event_never_reaches_a_tenant_A_stream()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var itemsOfB = new HashSet<Guid> { world.B.TaskId, world.B.RunId, world.B.PhaseId, world.B.ProjectId };

        foreach (var credential in await world.CredentialsOfAAsync())
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            session.CancelAfter(TimeSpan.FromMinutes(1));

            using var response = await credential.Http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var body = await response.Content.ReadAsStreamAsync(session.Token);
            using var reader = new StreamReader(body);
            var frames = Channel.CreateUnbounded<JsonElement>();
            _ = PumpAsync(reader, frames, session.Token);

            // B first: notifications arrive in commit order, so once A's frame is here B's would have been.
            JsonElement? ofA = null;
            for (var attempt = 0; attempt < 5 && ofA is null; attempt++)
            {
                Assert.False((await world.NoteAsync(world.B, $"tenant B event {attempt}")).Refused);
                Assert.False((await world.NoteAsync(world.A, $"tenant A event {attempt}")).Refused);

                while (await NextAsync(frames, TimeSpan.FromSeconds(4), session.Token) is { } frame)
                {
                    var item = frame.TryGetProperty("itemId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : (Guid?)null;
                    Assert.False(item is { } seen && itemsOfB.Contains(seen), $"{credential} streamed B's event: {frame}");
                    if (item == world.A.TaskId)
                    {
                        ofA = frame;
                        break;
                    }
                }
            }

            Assert.True(ofA is not null, $"{credential} never received A's own event");
            await session.CancelAsync();
        }
    }

    [Fact]
    public async Task A_tenant_A_cookie_resolves_links_in_A_and_never_names_B()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var cookie = await world.CookieAsync(world.UserA);

        var own = await cookie.GetAsync("/r/run-a/1.1", Ct);
        Assert.Equal(HttpStatusCode.Redirect, own.StatusCode);
        Assert.Equal($"/runs/{world.A.RunId}/{world.A.TaskId}", own.Headers.Location!.OriginalString);

        // B's path exists, but only in a tenant this user may not enter: the page says nothing of it.
        var other = await cookie.GetAsync($"/r/{TenantWorld.OnlyInBPath}", Ct);
        var page = await other.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.DoesNotContain(world.B.Slug, page, StringComparison.Ordinal);
        Assert.DoesNotContain(world.B.WorkspaceId.ToString(), page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-tenant", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_A_callers_mint_into_A_and_cannot_touch_B_sessions()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;

        var store = new SessionStore(fixture.DataSource);
        var inB = (await store.MintAsync(
            world.B.WorkspaceId,
            new MintRequest($"tenant-b-{Guid.CreateVersion7():n}"[..20], ActorRole.Worker, 1, false, null),
            Ct)).Minted!;

        foreach (var credential in await world.CredentialsOfAAsync())
        {
            var name = $"tenant-{credential.Kind}-{Guid.CreateVersion7():n}"[..24];
            var mint = await credential.Http.PostAsync("/api/agent-sessions", AppServer.Json(new { name, role = "worker" }), Ct);

            if (credential.Kind == "cookie")
            {
                // The cookie is not a bearer: it mints nowhere, B included.
                Assert.Equal(HttpStatusCode.Unauthorized, mint.StatusCode);
                continue;
            }

            Assert.Equal(HttpStatusCode.Created, mint.StatusCode);
            await using (var db = fixture.NewContext())
            {
                Assert.Equal(world.A.WorkspaceId, (await db.AgentSessions.SingleAsync(s => s.Name == name, Ct)).WorkspaceId);
            }

            Assert.Equal(HttpStatusCode.NotFound, (await credential.Http.DeleteAsync($"/api/agent-sessions/{inB.SessionId}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await credential.Http.PostAsync($"/api/agent-sessions/{inB.SessionId}/release", null, Ct)).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await credential.Http.PatchAsync($"/api/agent-sessions/{inB.SessionId}", AppServer.Json(new { claudeSessionId = "x" }), Ct)).StatusCode);
        }

        // B's session still authenticates, in B.
        await using var stillB = await world.McpAsync(inB.Token);
        var brief = await ReadServer.CallEitherAsync(stillB, "work_get", Args(("refs", new[] { "run-a/1.1" })), Ct);
        Assert.Contains(TenantWorld.TitleB, brief.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Test 4 of the brief: before user-oauth puts a tenant in the token, the token's user must have exactly
    /// one membership. With two it is refused — a 403 that says to sign in again — on every surface.
    /// </summary>
    [Fact]
    public async Task A_tenant_less_oauth_token_whose_user_has_two_memberships_is_refused_with_a_message()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        world.Legacy.On = true;

        var browser = await server.SignInAsync(Ct);
        var api = await world.OAuthAsync(browser, $"{server.BaseAddress}/api", "workstate.read workstate.sessions offline_access");
        var mcp = await world.OAuthAsync(browser, $"{server.BaseAddress}/mcp", "mcp offline_access");
        Assert.Equal(HttpStatusCode.OK, (await world.Bearer(api).GetAsync("/api/v1/overview", Ct)).StatusCode);

        await world.MemberAsync(AppServer.Username, world.B.WorkspaceId);

        foreach (var (token, request) in new (string, Func<HttpRequestMessage>)[]
                 {
                     (api, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/overview")),
                     (api, () => new HttpRequestMessage(HttpMethod.Get, "/api/stream")),
                     (api, () => new HttpRequestMessage(HttpMethod.Post, "/api/agent-sessions") { Content = AppServer.Json(new { name = "never", role = "worker" }) }),
                     (mcp, AppServer.McpInitialize),
                 })
        {
            var sent = request();
            var response = await world.Bearer(token).SendAsync(sent, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{sent.RequestUri} → {(int)response.StatusCode}: {body}");
            var error = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;
            Assert.Contains("member of several", error, StringComparison.Ordinal);
            Assert.Contains("Sign in again", error, StringComparison.Ordinal);
        }

        // One membership again, and the same token works: nothing about it was spent.
        await world.LeaveAsync(AppServer.Username, world.B.WorkspaceId);
        Assert.Equal(HttpStatusCode.OK, (await world.Bearer(api).GetAsync("/api/v1/overview", Ct)).StatusCode);
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);

    private static List<Guid> Ids(JsonElement resolved)
        => [.. resolved.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("id").GetGuid())];

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

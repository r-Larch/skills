using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using WorkState.Core.Domain;
using WorkState.Tests.App;
using WorkState.Tests.App.V1;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.AgentImport;

/// <summary>
/// work_import is hidden, not refused: [Authorize] removes a tool from tools/list. So the grant is
/// proven twice — the tool appears for a granted session, and every other session's tools/list is
/// the exact bytes it was before the tool existed.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ImportVisibilityTests(PostgresFixture fixture)
{
    /// <summary>
    /// SHA-256 of the serialized tools/list per profile without work_import: dc388f4's list, with
    /// work_search no longer promising doc hits and work_note's <c>to</c> no longer offering a selector;
    /// the orchestrator list re-pinned when seven orchestrator-only descriptions were trimmed to make
    /// room for work_session (hidden here, listed only with X-WorkState-Client); both re-pinned when
    /// work_brief's scope stopped promising the busiest run, which several projects now answer with a
    /// list (bare-brief); both re-pinned when work_brief's for: named gate and stopped falling back
    /// to resume for anything else (brief-variants).
    /// </summary>
    private const string WorkerList = "e871692a052d9ded175ca7675f9b8d0efee4b1a3fc693906bbaab80a0ee55615";
    private const string OrchestratorList = "1ef09a4188c5d356b83710360156bd04041791ec31c82dd9f3280d20db272a2c";
    private const string ControllerList = OrchestratorList;

    [Fact]
    public async Task Tools_list_without_the_import_grant_is_byte_identical_to_before_the_tool()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var (_, orchestrator) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-plain-o", role = "orchestrator" });
        var (_, controller) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-plain-c", role = "controller" });
        var (_, worker) = await MintAsync(server, ReadServer.ControllerToken, new { name = "aw-plain-w", role = "worker" });

        var seen = new List<string>();
        foreach (var (label, token, pinned) in new[]
                 {
                     ("static worker", ReadServer.WorkerToken, WorkerList),
                     ("static orchestrator", ReadServer.OrchestratorToken, OrchestratorList),
                     ("static controller", ReadServer.ControllerToken, ControllerList),
                     ("minted worker", Token(worker), WorkerList),
                     ("minted orchestrator", Token(orchestrator), OrchestratorList),
                     ("minted controller", Token(controller), ControllerList),
                 })
        {
            await using var client = await server.ConnectAsync(token, Ct);
            var (hash, names) = await ListAsync(client);
            Assert.DoesNotContain("work_import", names);
            if (hash != pinned)
            {
                seen.Add($"{label} {hash}");
            }
        }

        Assert.True(seen.Count == 0, string.Join("\n", seen));
    }

    [Fact]
    public async Task A_session_minted_with_the_import_grant_lists_work_import_and_its_imports_journal_under_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await BindAsync(world);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var (status, minted) = await MintAsync(
            server, ReadServer.ControllerToken, new { name = "aw-import", role = "orchestrator", grants = new[] { "import" } });
        Assert.Equal(HttpStatusCode.Created, status);

        await using var client = await server.ConnectAsync(Token(minted), Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var import = Assert.Single(tools, t => t.Name == "work_import");

        // A ratchet, by the budget test's measure (chars / 4): 538 for the tool, 6849 for the granted list.
        var cost = (JsonSerializer.Serialize(import.ProtocolTool).Length + 3) / 4;
        var total = (JsonSerializer.Serialize(tools.Where(t => t.Name.StartsWith("work_", StringComparison.Ordinal)).Select(t => t.ProtocolTool)).Length + 3) / 4;
        Assert.True(cost <= 540 && total <= 6850, $"work_import costs {cost} tokens in tools/list; the granted session's work_* list {total}");

        var text = await ReadServer.CallAsync(client, "work_import", new Dictionary<string, object?>
        {
            ["project"] = project,
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["source"] = new Dictionary<string, object?> { ["path"] = "TODO.md", ["anchor"] = "L3" },
                    ["kind"] = "followup",
                    ["title"] = "Wire the importer",
                    ["ref"] = "todo-1",
                    ["why"] = "nothing in mcp/ calls it yet",
                },
            },
        }, Ct);
        Assert.StartsWith($"OK  project {project} — 1 created", text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        var sessionId = minted.GetProperty("sessionId").GetGuid();
        var item = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "todo-1", Ct);
        var journal = await db.Events.SingleAsync(e => e.ItemId == item.Id && e.Kind == "import", Ct);
        Assert.Equal(sessionId, journal.SessionId);
        Assert.Equal(ActorRole.Orchestrator, journal.Role);

        var record = await db.ImportRecords.SingleAsync(r => r.ItemId == item.Id, Ct);
        Assert.Equal("aw-import", record.ImportedBy);
        Assert.Equal(sessionId, record.SessionId);

        var page = await V1Http.GetAsync(V1Http.Bearer(server, ReadServer.ControllerToken), $"/api/v1/items/{item.Id}");
        var imported = page.GetProperty("imported");
        Assert.Equal("TODO.md", imported.GetProperty("path").GetString());
        Assert.Equal("L3", imported.GetProperty("anchor").GetString());
        Assert.Equal("nothing in mcp/ calls it yet", imported.GetProperty("why").GetString());
        Assert.Equal("aw-import", imported.GetProperty("session").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_minted_controller_with_the_grant_lists_it_and_a_static_controller_never_does()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var (_, minted) = await MintAsync(
            server, ReadServer.ControllerToken, new { name = "aw-import-c", role = "controller", grants = new[] { "import" } });

        await using var granted = await server.ConnectAsync(Token(minted), Ct);
        await using var plain = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        Assert.Contains("work_import", (await ListAsync(granted)).Names);
        Assert.DoesNotContain("work_import", (await ListAsync(plain)).Names);
    }

    [Fact]
    public async Task The_grant_needs_an_orchestrator_minter_an_orchestrator_role_and_a_known_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var (byWorker, _) = await MintAsync(
            server, ReadServer.WorkerToken, new { name = "aw-g1", role = "worker", grants = new[] { "import" } });
        Assert.Equal(HttpStatusCode.Forbidden, byWorker);

        var (forWorker, body) = await MintAsync(
            server, ReadServer.OrchestratorToken, new { name = "aw-g2", role = "worker", grants = new[] { "import" } });
        Assert.Equal(HttpStatusCode.BadRequest, forWorker);
        Assert.Contains("role orchestrator or controller", body.GetProperty("error").GetString(), StringComparison.Ordinal);

        var (unknown, _) = await MintAsync(
            server, ReadServer.OrchestratorToken, new { name = "aw-g3", role = "orchestrator", grants = new[] { "admin" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown);

        await using var db = fixture.NewContext();
        Assert.False(await db.AgentSessions.AnyAsync(s => s.WorkspaceId == world.WorkspaceId, Ct));
    }

    private async Task<string> BindAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        var project = (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
        var bound = await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            project,
            new WorkState.Core.Writing.UpdateArgs(Repo: new WorkState.Core.Writing.RepoArg($"https://github.com/r-larch/v-{world.WorkspaceId:n}")),
            Ct);
        Assert.False(bound.Refused, bound.Text);
        return project;
    }

    internal static async Task<(string Hash, IReadOnlyList<string> Names)> ListAsync(McpClient client)
    {
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var payload = JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool));
        return (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), [.. tools.Select(t => t.Name)]);
    }

    internal static async Task<(HttpStatusCode Status, JsonElement Body)> MintAsync(AppServer server, string token, object body)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/agent-sessions", AppServer.Json(body), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    internal static string Token(JsonElement minted) => minted.GetProperty("token").GetString()!;
}

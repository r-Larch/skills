using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using WorkState.Core.Board;
using WorkState.Mcp;
using WorkState.Tests.AgentImport;
using WorkState.Tests.App;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 11 — a hand-started session on the shared static registration names itself: the session
/// helper sends X-WorkState-Client, work_session(name) binds it, and every later call carrying only
/// the key is signed with the name. A minted session's name stays its credential's.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SelfNamingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_key_only_call_after_work_session_is_attributed_to_the_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        var key = Key();

        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct, Client(key));

        var named = await ReadServer.CallAsync(
            client, "work_session", new Dictionary<string, object?> { ["name"] = "skills-5d" }, Ct);
        Assert.Contains("every call from this session is now signed skills-5d", named, StringComparison.Ordinal);

        await StartAsync(client, "1.1");
        await ReadServer.CallAsync(
            client, "work_note", new Dictionary<string, object?> { ["kind"] = "observation", ["body"] = "keyed" }, Ct);
        await VerifyAsync(client);

        await using var db = fixture.NewContext();
        var session = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("skills-5d", session.Name);
        Assert.Equal(key, session.ClientKey);

        Assert.Equal(session.Id, (await db.Claims.SingleAsync(c => c.ItemId == world.TaskId, Ct)).SessionId);
        Assert.Equal(session.Id, (await db.Notes.SingleAsync(n => n.WorkspaceId == world.WorkspaceId, Ct)).SessionId);
        Assert.Equal(session.Id, (await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct)).SessionId);

        var brief = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        Assert.DoesNotContain("session  unnamed", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_keys_cannot_hold_one_live_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        var first = Key();

        await using var holder = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct, Client(first));
        await using var rival = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct, Client(Key()));

        await ReadServer.CallAsync(holder, "work_session", Name("autowork-7c"), Ct);
        var (text, refused) = await ReadServer.CallEitherAsync(rival, "work_session", Name("autowork-7c"), Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("\"autowork-7c\" is taken", text, StringComparison.Ordinal);
        Assert.Contains("last seen", text, StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            Assert.Equal(first, (await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct)).ClientKey);
        }

        // A holder silent for longer than the lease has let go of the name.
        await using (var db = fixture.NewContext())
        {
            var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
            row.LastSeenAt = DateTimeOffset.UtcNow.AddHours(-2);
            await db.SaveChangesAsync(Ct);
        }

        var (taken, refusedAgain) = await ReadServer.CallEitherAsync(rival, "work_session", Name("autowork-7c"), Ct);
        Assert.False(refusedAgain, taken);
    }

    [Fact]
    public async Task A_minted_token_ignores_both_headers_and_does_not_list_work_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var (_, minted) = await ImportVisibilityTests.MintAsync(
            server, ReadServer.ControllerToken, new { name = "aw-minted", role = "worker" });

        await using var client = await server.ConnectAsync(
            ImportVisibilityTests.Token(minted),
            Ct,
            new Dictionary<string, string>
            {
                [CallerSession.AgentHeader] = "skills-5d",
                [CallerSession.ClientHeader] = Key(),
            });

        Assert.DoesNotContain("work_session", (await ImportVisibilityTests.ListAsync(client)).Names);

        await VerifyAsync(client);

        await using var db = fixture.NewContext();
        var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("aw-minted", row.Name);
        Assert.Null(row.ClientKey);
        Assert.Equal(row.Id, (await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct)).SessionId);
    }

    [Fact]
    public async Task An_agent_header_with_a_key_binds_the_key_to_that_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);
        var key = Key();

        await using (var both = await server.ConnectAsync(
                         ReadServer.WorkerToken,
                         Ct,
                         new Dictionary<string, string> { [CallerSession.AgentHeader] = "m27-probe", [CallerSession.ClientHeader] = key }))
        {
            await ReadServer.CallAsync(both, "work_brief", null, Ct);
        }

        await using var keyOnly = await server.ConnectAsync(ReadServer.WorkerToken, Ct, Client(key));
        await VerifyAsync(keyOnly);

        await using var db = fixture.NewContext();
        var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId, Ct);
        Assert.Equal("m27-probe", row.Name);
        Assert.Equal(key, row.ClientKey);
        Assert.Equal(row.Id, (await db.Verifications.SingleAsync(v => v.ItemId == world.TaskId, Ct)).SessionId);
    }

    [Fact]
    public async Task Without_agent_or_key_a_call_is_unnamed_and_an_unbound_key_is_too()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        await using var bare = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        await using var keyed = await server.ConnectAsync(ReadServer.WorkerToken, Ct, Client(Key()));

        await VerifyAsync(bare);
        await VerifyAsync(keyed);

        await using var db = fixture.NewContext();
        Assert.False(await db.AgentSessions.AnyAsync(s => s.WorkspaceId == world.WorkspaceId, Ct));
        Assert.All(
            await db.Verifications.Where(v => v.ItemId == world.TaskId).ToListAsync(Ct),
            v => Assert.Null(v.SessionId));
    }

    [Fact]
    public async Task The_unnamed_brief_names_work_session_with_a_key_and_the_setup_without()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        await using var keyed = await server.ConnectAsync(ReadServer.WorkerToken, Ct, Client(Key()));
        await using var bare = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var withKey = (await ReadServer.CallAsync(keyed, "work_brief", null, Ct)).Split('\n');
        var without = (await ReadServer.CallAsync(bare, "work_brief", null, Ct)).Split('\n');

        Assert.Equal(BoardReader.UnnamedKeyed, withKey[1]);
        Assert.Equal(BoardReader.UnnamedBare, without[1]);
        Assert.All(withKey.Concat(without), line => Assert.True(line.Length <= 100, $"{line.Length} columns: {line}"));
    }

    /// <summary>
    /// Hidden, not refused, like work_import: only a static token whose request carries the helper's
    /// key lists it, so every other tools/list stays the bytes ImportVisibilityTests pins.
    /// </summary>
    [Fact]
    public async Task work_session_is_listed_only_for_a_static_token_with_a_client_key()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        var measured = new List<string>();
        foreach (var (token, budget) in new[]
                 {
                     (ReadServer.WorkerToken, 2200), (ReadServer.OrchestratorToken, 6400), (ReadServer.ControllerToken, 6400),
                 })
        {
            await using var withKey = await server.ConnectAsync(token, Ct, Client(Key()));
            await using var without = await server.ConnectAsync(token, Ct);

            var listed = await withKey.ListToolsAsync(cancellationToken: Ct);
            var plain = await without.ListToolsAsync(cancellationToken: Ct);

            Assert.Contains(listed, t => t.Name == "work_session");
            Assert.DoesNotContain(plain, t => t.Name == "work_session");

            measured.Add($"{Tokens(listed)} vs {Tokens(plain)}");

            // The keyed session is the hand-started one this exists for; it pays ReadToolTests' budget too.
            Assert.True(Tokens(listed) <= budget, $"keyed tools/list {Tokens(listed)} tokens, budget {budget}");
        }

        // A ratchet, by the budget test's measure (chars / 4): 147-148 tokens measured on every profile.
        var delta = measured.Select(m => m.Split(" vs ")).Select(p => int.Parse(p[0]) - int.Parse(p[1])).ToList();
        Assert.True(delta.All(d => d <= 170), string.Join("; ", measured));
    }

    [Fact]
    public async Task A_name_from_another_launcher_is_refused_to_work_session()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world);

        await using (var launched = await server.ConnectAsync(
                         ReadServer.WorkerToken,
                         Ct,
                         new Dictionary<string, string>
                         {
                             [CallerSession.AgentHeader] = "aw-x",
                             [CallerSession.LaunchedByHeader] = "foreman",
                         }))
        {
            await ReadServer.CallAsync(launched, "work_brief", null, Ct);
        }

        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct, Client(Key()));
        var (text, refused) = await ReadServer.CallEitherAsync(client, "work_session", Name("aw-x"), Ct);

        Assert.True(refused, text);
        Assert.Contains("foreman", text, StringComparison.Ordinal);
    }

    private static int Tokens(IEnumerable<McpClientTool> tools)
        => (JsonSerializer.Serialize(tools.Where(t => t.Name.StartsWith("work_", StringComparison.Ordinal)).Select(t => t.ProtocolTool)).Length + 3) / 4;

    private Task<AppServer> StartAsync(WriteWorld world)
        => AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

    private static string Key() => $"host/{Random.Shared.Next(1000, 99999)}/{Guid.NewGuid():n}";

    private static Dictionary<string, string> Client(string key) => new() { [CallerSession.ClientHeader] = key };

    private static Dictionary<string, object?> Name(string name) => new() { ["name"] = name };

    private static async Task StartAsync(McpClient client, string reference)
    {
        var (text, refused) = await ReadServer.CallEitherAsync(
            client,
            "work_status",
            new Dictionary<string, object?> { ["ref"] = reference, ["to"] = "in_progress", ["tree"] = $"p:/wt-{Guid.NewGuid():n}"[..18] },
            Ct);
        Assert.False(refused, text);
    }

    private static Task<string> VerifyAsync(McpClient client)
        => ReadServer.CallAsync(
            client,
            "work_verify",
            new Dictionary<string, object?> { ["ref"] = "1.1", ["result_line"] = "Passed! Failed: 0, Passed: 1" },
            Ct);
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WorkState.Api.App.V1;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// The v1 tests' world: <see cref="WriteSeed"/>'s project, run and phases, with one of everything
/// the read API reports seeded through the real writer where the writer is what produces it — a
/// session-held claim, a report, a verification, a note — and straight through EF where a state is
/// all that matters.
/// </summary>
internal sealed record V1World(
    WriteWorld World,
    Guid SessionId,
    Guid Unverified,
    Guid WaitsOnWork,
    Guid BlockedBehindWork,
    Guid BlockedOnPerson,
    Guid Blocker,
    Guid Deferred,
    string Tree)
{
    public const string ReaderToken = "test-reader-token-7e0d42";

    public const string SessionName = "aw-v1";


    public static async Task<V1World> CreateAsync(PostgresFixture fixture)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var writer = world.Writer;

        // A path tree is locked server-wide, so each world claims one of its own.
        var tree = $"P:/trees/v1-{world.RunId:n}";

        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "the board carries criteria", ticked: true);
        await WriteSeed.CriterionAsync(world, world.TaskId, 2, "the page splits reports from verifications", ticked: false);

        var resolved = await new SessionStore(fixture.DataSource).ResolveAsync(
            world.WorkspaceId,
            ActorRole.Orchestrator,
            new SessionFacts(SessionName, ClaudeSessionId: "claude-v1", LaunchedBy: "skills-5d", ClientName: "claude-code", ClientVersion: "2.1"),
            Ct);
        var session = resolved.Session ?? throw new Xunit.Sdk.XunitException(resolved.Refused);
        var actor = WriteSeed.Orchestrator with { SessionId = session.Id, Session = session.Name };

        Ok(await writer.StatusAsync(world.WorkspaceId, "1.1", "in_progress", actor, new StatusArgs(Tree: tree), Ct));
        Ok(await writer.ReportAsync(
            world.WorkspaceId,
            "1.1",
            actor,
            new ReportArgs("done", ["a.cs", "b.cs"], Verify: "dotnet test → Passed! 91", RedCheck: "broke line 4, went red"),
            Ct));
        Ok(await writer.VerifyAsync(
            world.WorkspaceId, "1.1", actor, "dotnet test --filter Guard", "Passed! Failed: 0, Passed: 91", true,
            "orchestrator", "{\"passed\":91}", "red at line 4", Ct));
        Ok(await writer.NoteAsync(world.WorkspaceId, actor, "decision", "address by id", "1.1", null, Ct));

        // Closed with nothing independent behind it: the honesty count and an inbox row.
        var unverified = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);

        // Waiting on work, not on a person: neither is an inbox row.
        var gate = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4");
        var waits = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");
        var behind = await WriteSeed.ChildAsync(world, world.PhaseId, "1.5", status: ItemStatus.Blocked);
        await WriteSeed.LinkAsync(world, gate, waits, LinkType.Blocks);
        await WriteSeed.LinkAsync(world, gate, behind, LinkType.Blocks);

        // Blocked with nothing open in its way: that waits on a decision.
        var person = await WriteSeed.ChildAsync(world, world.PhaseId, "1.6", status: ItemStatus.Blocked);

        // A deferral: done/obsolete, which the honesty count must not call unverified.
        var deferred = await WriteSeed.ChildAsync(world, world.PhaseId, "1.7", status: ItemStatus.Done);
        var blocker = await WriteSeed.ChildAsync(world, world.TaskId, "F-1", kind: "finding", severity: "blocker");

        await using (var db = fixture.NewContext())
        {
            var obsolete = await db.Items.FindAsync([deferred], Ct);
            obsolete!.Resolution = ItemResolution.Obsolete;

            db.Artifacts.Add(new Artifact
            {
                Id = Guid.CreateVersion7(),
                ItemId = world.TaskId,
                Kind = "commit",
                Value = "a439808f00",
                At = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(Ct);
        }

        return new V1World(world, session.Id, unverified, waits, behind, person, blocker, deferred, tree.ToLowerInvariant());
    }

    private static void Ok(WriteResult result) => Assert.False(result.Refused, result.Text);

    public Task<AppServer> StartAsync(PostgresFixture fixture)
        => V1Http.StartAsync(fixture, World.Slug);
}

internal static class V1Http
{
    public static Task<AppServer> StartAsync(PostgresFixture fixture, string slug, string? readerToken = V1World.ReaderToken)
        => AppServer.StartAsync(fixture.ConnectionString, slug, Ct, readerToken: readerToken);

    public static HttpClient Bearer(AppServer server, string token)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static HttpClient Reader(AppServer server) => Bearer(server, V1World.ReaderToken);

    /// <summary>A v1 body, after asserting rule 3 and rule 4 hold on it.</summary>
    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} → {(int)response.StatusCode}: {body}");
        Assert.Equal(ApiV1.Version, Assert.Single(response.Headers.GetValues(ApiV1.Header)));

        var json = JsonDocument.Parse(body).RootElement.Clone();
        Assert.True(json.GetProperty("asOf").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(JsonValueKind.Number, json.GetProperty("seq").ValueKind);
        return json;
    }

    public static IReadOnlyList<JsonElement> Array(JsonElement json, string name)
        => [.. json.GetProperty(name).EnumerateArray()];

    public static JsonElement ByRef(IEnumerable<JsonElement> rows, string reference, string itemProperty = "item")
        => rows.FirstOrDefault(r => r.GetProperty(itemProperty).GetProperty("ref").GetString() == reference)
               is { ValueKind: JsonValueKind.Object } found
            ? found
            : throw new Xunit.Sdk.XunitException($"no row for {reference}");

    public static bool HasRef(IEnumerable<JsonElement> rows, string reference, string itemProperty = "item")
        => rows.Any(r => r.GetProperty(itemProperty).GetProperty("ref").GetString() == reference);
}

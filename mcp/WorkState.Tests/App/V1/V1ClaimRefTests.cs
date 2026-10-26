using System.Text.Json;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.App.V1.V1Http;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// Unit <c>mature/ui-claims-refs</c>: every Claim names its holding session in
/// <c>heldBySession</c>, and every <c>qualifiedRef</c> the dashboard copies resolves to its item alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1ClaimRefTests(PostgresFixture fixture)
{
    private const string MintedName = "aw-m24-held";

    [Fact]
    public async Task Every_claim_names_its_minted_session_and_a_sessionless_claim_names_none()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var other = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        var mint = await new SessionStore(fixture.DataSource).MintAsync(
            world.WorkspaceId, new MintRequest(MintedName, ActorRole.Controller, 1, false, null), Ct);
        var minted = mint.Minted ?? throw new Xunit.Sdk.XunitException(mint.Conflict);
        var held = new Actor("controller", ActorRole.Controller) { SessionId = minted.SessionId, Session = minted.Name };

        Ok(await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "in_progress", held, new StatusArgs(Tree: $"P:/trees/m24-a-{world.RunId:n}"), Ct));
        Ok(await world.Writer.StatusAsync(
            world.WorkspaceId, "1.2", "in_progress", WriteSeed.Orchestrator, new StatusArgs(Tree: $"P:/trees/m24-b-{world.RunId:n}"), Ct));

        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        var reader = Reader(server);

        var overview = await GetAsync(reader, "/api/v1/overview");
        var working = Array(overview, "workingNow");
        Assert.Equal(2, working.Count);
        AssertMinted(ByRef(working, "1.1"));
        AssertSessionless(ByRef(working, "1.2"));

        var rows = Array(await GetAsync(reader, $"/api/v1/runs/{world.RunId}/board"), "rows");
        AssertMinted(ByRef(rows, "1.1").GetProperty("claim"));
        AssertSessionless(ByRef(rows, "1.2").GetProperty("claim"));

        AssertMinted((await GetAsync(reader, $"/api/v1/items/{world.TaskId}")).GetProperty("claim"));
        AssertSessionless((await GetAsync(reader, $"/api/v1/items/{other}")).GetProperty("claim"));

        var sessions = Array(await GetAsync(reader, $"/api/v1/sessions?name={MintedName}"), "sessions");
        AssertMinted(Assert.Single(Assert.Single(sessions).GetProperty("claims").EnumerateArray()));
    }

    [Fact]
    public async Task Every_qualified_ref_resolves_to_its_item_alone_even_when_the_bare_ref_is_shared()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var nineOne = await WriteSeed.SecondRunAsync(world, "run-b");
        Guid runB, twin;

        await using (var db = fixture.NewContext())
        {
            runB = (await db.Items.FindAsync([nineOne], Ct))!.ParentId!.Value;
            var shared = Seed.NewItem(world.WorkspaceId, runB, "task", "1.1", 2m, "The other 1.1");
            db.Items.Add(shared);
            await db.SaveChangesAsync(Ct);
            twin = shared.Id;
        }

        var queries = new BoardQueries(fixture.DataSource);
        Assert.Equal(2, (await queries.ResolveAsync(world.WorkspaceId, "1.1", Ct)).Count);

        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        var reader = Reader(server);

        var refs = new List<JsonElement>();
        foreach (var run in new[] { world.RunId, runB })
        {
            var board = await GetAsync(reader, $"/api/v1/runs/{run}/board");
            refs.Add(board.GetProperty("run"));
            refs.Add(board.GetProperty("project"));
            refs.AddRange(Array(board, "rows").Select(r => r.GetProperty("item")));
        }

        foreach (var item in new[] { world.TaskId, twin })
        {
            var page = await GetAsync(reader, $"/api/v1/items/{item}");
            refs.Add(page.GetProperty("item"));
            refs.AddRange(Array(page, "path"));
        }

        var overview = await GetAsync(reader, "/api/v1/overview");
        foreach (var project in Array(overview, "projects"))
        {
            refs.Add(project.GetProperty("item"));
            refs.AddRange(Array(project, "runs").Select(r => r.GetProperty("item")));
        }

        foreach (var shown in refs)
        {
            var callable = shown.GetProperty("qualifiedRef").GetString()!;
            var resolved = await queries.ResolveAsync(world.WorkspaceId, callable, Ct);
            Assert.True(resolved.Count == 1, $"{callable} resolves to {resolved.Count} items");
            Assert.Equal(shown.GetProperty("id").GetGuid(), resolved[0].Id);
        }

        var both = refs.Where(r => r.GetProperty("ref").GetString() == "1.1")
            .Select(r => (Id: r.GetProperty("id").GetGuid(), Callable: r.GetProperty("qualifiedRef").GetString()))
            .Distinct()
            .ToList();
        Assert.Equal(new HashSet<Guid> { world.TaskId, twin }, both.Select(b => b.Id).ToHashSet());
        Assert.All(both, b => Assert.NotEqual("1.1", b.Callable));
    }

    private static void AssertMinted(JsonElement claim)
    {
        Assert.Equal(MintedName, claim.GetProperty("heldBySession").GetString());
        Assert.Equal("controller", claim.GetProperty("role").GetString());
    }

    private static void AssertSessionless(JsonElement claim)
    {
        Assert.Equal(JsonValueKind.Null, claim.GetProperty("heldBySession").ValueKind);
        Assert.Equal(JsonValueKind.Null, claim.GetProperty("session").ValueKind);
        Assert.Equal("orchestrator", claim.GetProperty("role").GetString());
    }

    private static void Ok(WriteResult result) => Assert.False(result.Refused, result.Text);
}

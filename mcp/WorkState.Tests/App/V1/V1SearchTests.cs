using System.Net;
using System.Text.Json;
using WorkState.Api.App.V1;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.App.V1.V1Http;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// m49: the dashboard's find. Exact refs by <c>/resolve</c>'s rule, then ref prefixes, then full
/// text — each item once, and only ever from the request's own workspace.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1SearchTests(PostgresFixture fixture)
{
    private sealed record Seeded(V1World V1, string ProjectRef, Guid Seam, Guid Twelve, Guid Zeppelin, Guid Lantern);

    private async Task<Seeded> SeedAsync()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var world = v1.World;

        await using var db = fixture.NewContext();
        var project = await db.Items.FindAsync([world.ProjectId], Ct);

        var m = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", "m", 7m, "Milestone m");
        var seam = Seed.NewItem(world.WorkspaceId, world.PhaseId, "task", "spa-blank-page-seam", 20m, "The dashboard renders nothing");
        var zeppelin = Seed.NewItem(world.WorkspaceId, world.PhaseId, "task", "1.20", 21m, "Moor the zeppelin before the storm");
        var lantern = Seed.NewItem(world.WorkspaceId, world.PhaseId, "task", "lantern", 22m, "Lantern lights the way");
        db.Items.AddRange(m, seam, zeppelin, lantern);
        await db.SaveChangesAsync(Ct);

        var twelve = Seed.NewItem(world.WorkspaceId, m.Id, "task", "12", 1m, "Twelfth step");
        db.Items.Add(twelve);
        await db.SaveChangesAsync(Ct);

        return new Seeded(v1, project!.Ref, seam.Id, twelve.Id, zeppelin.Id, lantern.Id);
    }

    private static async Task<IReadOnlyList<JsonElement>> SearchAsync(HttpClient client, string q)
        => Array(await GetAsync(client, $"/api/v1/search?q={Uri.EscapeDataString(q)}"), "hits");

    private static Guid Id(JsonElement hit) => hit.GetProperty("item").GetProperty("id").GetGuid();

    private static string Match(JsonElement hit) => hit.GetProperty("match").GetString()!;

    [Fact]
    public async Task Search_puts_an_exact_ref_first_in_every_form_resolve_takes()
    {
        var seeded = await SeedAsync();
        var world = seeded.V1.World;
        await using var server = await seeded.V1.StartAsync(fixture);
        var reader = Reader(server);

        var cases = new (string Query, Guid Expected)[]
        {
            ("spa-blank-page-seam", seeded.Seam),
            ("SPA-Blank-Page-Seam", seeded.Seam),
            ("m/12", seeded.Twelve),
            ("run-a/1.1", world.TaskId),
            ($"{seeded.ProjectRef}/run-a/1.1", world.TaskId),
            ($"{seeded.ProjectRef}/spa-blank-page-seam", seeded.Seam),
        };

        foreach (var (query, expected) in cases)
        {
            var hits = await SearchAsync(reader, query);
            Assert.True(hits.Count > 0, $"{query} → no hits");
            Assert.Equal(expected, Id(hits[0]));
            Assert.Equal("ref", Match(hits[0]));
        }

        // The hit is placed: the run and project the dashboard links and names it by.
        var seam = (await SearchAsync(reader, "spa-blank-page-seam"))[0];
        Assert.Equal(world.RunId, seam.GetProperty("run").GetProperty("id").GetGuid());
        Assert.Equal(world.ProjectId, seam.GetProperty("project").GetProperty("id").GetGuid());
        Assert.Equal($"{seeded.ProjectRef}/run-a/spa-blank-page-seam", seam.GetProperty("item").GetProperty("fullRef").GetString());
    }

    [Fact]
    public async Task Search_finds_a_ref_by_its_prefix_and_an_item_by_a_title_word()
    {
        var seeded = await SeedAsync();
        await using var server = await seeded.V1.StartAsync(fixture);
        var reader = Reader(server);

        var prefix = await SearchAsync(reader, "Spa-Blank");
        var seam = Assert.Single(prefix, h => Id(h) == seeded.Seam);
        Assert.Equal("prefix", Match(seam));

        var text = await SearchAsync(reader, "zeppelin");
        var zeppelin = Assert.Single(text, h => Id(h) == seeded.Zeppelin);
        Assert.Equal("text", Match(zeppelin));
        Assert.Contains("zeppelin", zeppelin.GetProperty("snippet").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", zeppelin.GetProperty("snippet").GetString(), StringComparison.Ordinal);

        // A prefix is literal: LIKE's wildcards in the query expand to nothing. (Full text may still
        // find the item by its ref's words, so only the prefix stage is asserted.)
        foreach (var wild in (string[])["spa_blank", "%seam", "spa%"])
        {
            Assert.DoesNotContain(await SearchAsync(reader, wild), h => Id(h) == seeded.Seam && Match(h) == "prefix");
        }
    }

    [Fact]
    public async Task Search_lists_an_item_matching_all_three_ways_once_at_its_best_match()
    {
        var seeded = await SeedAsync();
        await using var server = await seeded.V1.StartAsync(fixture);
        var reader = Reader(server);

        var hits = await SearchAsync(reader, "lantern");

        var lantern = Assert.Single(hits, h => Id(h) == seeded.Lantern);
        Assert.Equal("ref", Match(lantern));
        Assert.Equal(seeded.Lantern, Id(hits[0]));
        Assert.Equal(hits.Count, hits.Select(Id).Distinct().Count());

        var limited = Array(await GetAsync(reader, "/api/v1/search?q=1&limit=1"), "hits");
        Assert.Single(limited);
    }

    [Fact]
    public async Task Search_never_returns_another_workspaces_item_with_the_same_ref_or_title()
    {
        var seeded = await SeedAsync();
        var other = await WriteSeed.CreateAsync(fixture);
        var twin = await WriteSeed.ChildAsync(other, other.PhaseId, "spa-blank-page-seam");
        var namesake = await WriteSeed.ChildAsync(other, other.PhaseId, "lantern");
        await using (var db = fixture.NewContext())
        {
            (await db.Items.FindAsync([twin], Ct))!.Title = "Moor the zeppelin before the storm";
            await db.SaveChangesAsync(Ct);
        }

        await using var server = await seeded.V1.StartAsync(fixture);
        var reader = Reader(server);

        foreach (var query in (string[])["spa-blank-page-seam", "spa-blank", "zeppelin", "lantern", "1.1", "run-a/1.1"])
        {
            var ids = (await SearchAsync(reader, query)).Select(Id).ToList();
            Assert.DoesNotContain(twin, ids);
            Assert.DoesNotContain(namesake, ids);
            Assert.DoesNotContain(other.TaskId, ids);
        }

        Assert.Contains(seeded.Seam, (await SearchAsync(reader, "spa-blank")).Select(Id));
    }

    [Fact]
    public async Task Search_without_a_credential_is_401_and_without_a_query_is_400()
    {
        var seeded = await SeedAsync();
        await using var server = await seeded.V1.StartAsync(fixture);

        var anonymous = await server.NewClient().GetAsync("/api/v1/search?q=lantern", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(ApiV1.Version, Assert.Single(anonymous.Headers.GetValues(ApiV1.Header)));

        var reader = Reader(server);
        foreach (var path in (string[])["/api/v1/search", "/api/v1/search?q=", "/api/v1/search?q=%20%20"])
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(path, Ct)).StatusCode);
        }

        // The cookie reads it like every v1 route.
        Assert.NotEmpty(await SearchAsync(await server.SignInAsync(Ct), "lantern"));
    }
}

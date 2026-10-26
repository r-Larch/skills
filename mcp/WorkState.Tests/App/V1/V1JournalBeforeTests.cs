using System.Net;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.App.V1.V1Http;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// journal-before: "load older" in one call — the newest <c>limit</c> events below a seq, ascending,
/// with the same filters as the forward page, and never combined with <c>since</c>.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1JournalBeforeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Before_returns_the_newest_events_below_the_seq_in_ascending_order()
    {
        var (world, server) = await NotedAsync(8);
        await using var _ = server;
        var reader = Reader(server);

        var all = await SeqsAsync(reader, $"/api/v1/journal?scope={world.RunId}&since=0&limit=500");
        Assert.True(all.Count >= 8, $"fixture needs several events; it has {all.Count}");

        var below = all[^3];
        var page = await GetAsync(reader, $"/api/v1/journal?scope={world.RunId}&before={below}&limit=3");
        var seqs = Seqs(page);

        Assert.Equal(all.Where(s => s < below).TakeLast(3), seqs);
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs[0], page.GetProperty("cursor").GetInt64());
        Assert.True(page.GetProperty("hasMore").GetBoolean(), "older events remain below the page");

        var older = await GetAsync(reader, $"/api/v1/journal?scope={world.RunId}&before={seqs[0]}&limit=500");
        Assert.Equal(all.Where(s => s < seqs[0]), Seqs(older));
        Assert.False(older.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Before_beyond_the_newest_seq_is_the_newest_page_and_below_the_first_is_empty()
    {
        var (world, server) = await NotedAsync(4);
        await using var _ = server;
        var reader = Reader(server);

        var all = await SeqsAsync(reader, $"/api/v1/journal?scope={world.RunId}&since=0&limit=500");

        var beyond = await GetAsync(reader, $"/api/v1/journal?scope={world.RunId}&before={long.MaxValue}&limit=2");
        Assert.Equal(all.TakeLast(2), Seqs(beyond));
        Assert.True(beyond.GetProperty("hasMore").GetBoolean());

        var none = await GetAsync(reader, $"/api/v1/journal?scope={world.RunId}&before={all[0]}");
        Assert.Empty(Seqs(none));
        Assert.False(none.GetProperty("hasMore").GetBoolean());
        Assert.Equal(all[0], none.GetProperty("cursor").GetInt64());
    }

    [Fact]
    public async Task Before_applies_the_same_scope_and_kind_filters_as_the_forward_page()
    {
        var (world, server) = await NotedAsync(3);
        await using var _ = server;
        var reader = Reader(server);

        await WriteSeed.SecondRunAsync(world);
        var other = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "observation", "elsewhere", "9.1", null, Ct);
        Assert.False(other.Refused, other.Text);

        var notes = await SeqsAsync(reader, $"/api/v1/journal?scope={world.RunId}&since=0&kinds=note&limit=500");
        Assert.True(notes.Count >= 3, $"fixture needs several notes; it has {notes.Count}");

        var page = await GetAsync(reader, $"/api/v1/journal?scope={world.RunId}&before={long.MaxValue}&kinds=note&limit=500");

        Assert.Equal(notes, Seqs(page));
        Assert.All(Array(page, "events"), e => Assert.Equal("note", e.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Before_with_since_is_a_400_that_says_why()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(fixture, world.Slug);

        var response = await Reader(server).GetAsync("/api/v1/journal?since=1&before=5", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("since= and before= cannot be combined", body, StringComparison.Ordinal);
    }

    private async Task<(WriteWorld World, AppServer Server)> NotedAsync(int notes)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        for (var n = 0; n < notes; n++)
        {
            var wrote = await world.Writer.NoteAsync(
                world.WorkspaceId, WriteSeed.Orchestrator, "observation", $"note {n}", "1.1", null, Ct);
            Assert.False(wrote.Refused, wrote.Text);
        }

        return (world, await StartAsync(fixture, world.Slug));
    }

    private static async Task<IReadOnlyList<long>> SeqsAsync(HttpClient reader, string path)
        => Seqs(await GetAsync(reader, path));

    private static IReadOnlyList<long> Seqs(System.Text.Json.JsonElement page)
        => [.. Array(page, "events").Select(e => e.GetProperty("seq").GetInt64())];
}

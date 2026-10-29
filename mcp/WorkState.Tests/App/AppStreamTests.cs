using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using WorkState.Api.App.V1;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The live board and the cursor that makes it trustworthy. The stream is fed by the
/// <c>pg_notify('workstate', …)</c> trigger the journal already carries, so what is asserted here is
/// that a <em>database</em> write reaches a connected dashboard, and that a dashboard which was
/// disconnected across some writes can ask for exactly the ones it missed.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AppStreamTests(PostgresFixture fixture)
{
    private static readonly Actor Author = new("orchestrator-a", ActorRole.Orchestrator);

    [Fact]
    public async Task The_stream_delivers_an_event_caused_by_a_write_and_the_payload_carries_its_seq()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(1));

        var response = await client.GetAsync(
            "/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync(session.Token);
        using var reader = new StreamReader(body);

        var frames = Channel.CreateUnbounded<string>();
        _ = PumpAsync(reader, frames, session.Token);

        // The subscriber registers just after the response headers are flushed, so a write issued
        // the same instant can legitimately miss it. Retried rather than slept on: every attempt
        // is a real write, and the frame that arrives must name one of them.
        var written = new List<long>();
        string? frame = null;

        for (var attempt = 0; attempt < 5 && frame is null; attempt++)
        {
            written.Add(await NoteAsync(world, $"live event {attempt}"));
            frame = await NextAsync(frames, TimeSpan.FromSeconds(4), session.Token);
        }

        Assert.NotNull(frame);

        var delivered = JsonDocument.Parse(frame!).RootElement;
        var seq = delivered.GetProperty("seq").GetInt64();

        Assert.Contains(seq, written);
        Assert.Equal("note", delivered.GetProperty("kind").GetString());

        // And the seq is a real cursor into the journal, not a number the stream invented.
        var caughtUp = await AppServer.ReadJsonAsync(client, $"/api/v1/journal?scope={world.RunId}&since={seq - 1}", Ct);
        Assert.Contains(
            caughtUp.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("seq").GetInt64() == seq);
    }

    /// <summary>
    /// sse-heartbeat-test. An idle stream writes an SSE comment on the configured interval, so a
    /// proxy does not close a board that simply has nothing to say.
    /// </summary>
    [Fact]
    public async Task An_idle_stream_sends_a_heartbeat_comment_on_the_configured_interval()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct, heartbeat: "00:00:00.200");
        var client = await server.SignInAsync(Ct);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromSeconds(30));

        var response = await client.GetAsync(
            "/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(session.Token);
        using var reader = new StreamReader(body);

        using var within = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        within.CancelAfter(TimeSpan.FromSeconds(10));

        string? line;
        do
        {
            line = await reader.ReadLineAsync(within.Token);
        }
        while (line is not null && line != ": heartbeat");

        Assert.Equal(": heartbeat", line);
    }

    /// <summary>
    /// The workspace row is written by the first write, so a fresh server has none. The board must
    /// still connect: a 503 here is what the dashboard renders as "stream down" on day one.
    /// </summary>
    [Fact]
    public async Task On_a_server_with_nothing_in_it_the_stream_opens()
    {
        await using var server = await StartAsync($"cold-{Guid.CreateVersion7():n}");
        var client = await server.SignInAsync(Ct);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(1));

        var response = await client.GetAsync(
            "/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>doc 10 rule 4: the stream is part of the read API, so it carries its header, refused or open.</summary>
    [Fact]
    public async Task The_stream_carries_the_api_header_when_open_and_when_refused()
    {
        await using var server = await StartAsync($"header-{Guid.CreateVersion7():n}");

        var refused = await server.NewClient().GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(ApiV1.Version, Assert.Single(refused.Headers.GetValues(ApiV1.Header)));

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(1));
        var open = await (await server.SignInAsync(Ct)).GetAsync(
            "/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(ApiV1.Version, Assert.Single(open.Headers.GetValues(ApiV1.Header)));
    }

    /// <summary>
    /// A stream opened before the workspace existed has to come alive when the first write creates
    /// it — on the same connection, without the operator reloading the page.
    /// </summary>
    [Fact]
    public async Task A_stream_opened_on_an_empty_server_delivers_the_first_write()
    {
        var slug = $"cold-{Guid.CreateVersion7():n}";
        await using var server = await StartAsync(slug);
        var client = await server.SignInAsync(Ct);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(1));

        var response = await client.GetAsync(
            "/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(session.Token);
        using var reader = new StreamReader(body);

        var frames = Channel.CreateUnbounded<string>();
        _ = PumpAsync(reader, frames, session.Token);

        var writer = WriteSeed.Writer(fixture);
        var workspaceId = await writer.EnsureWorkspaceAsync(slug, Ct);

        // Retried for the same reason as above; the first attempt is the write that created the
        // workspace, and every later one is still a write the stream predates.
        string? frame = null;

        for (var attempt = 0; attempt < 5 && frame is null; attempt++)
        {
            var written = await writer.CreateAsync(
                workspaceId, WriteSeed.Orchestrator, [new NewItemArg("project", $"Repo {attempt}", Ref: $"p{attempt}")], Ct);
            Assert.False(written.Refused, written.Text);

            frame = await NextAsync(frames, TimeSpan.FromSeconds(4), session.Token);
        }

        Assert.NotNull(frame);

        var seq = JsonDocument.Parse(frame!).RootElement.GetProperty("seq").GetInt64();

        await using var db = fixture.NewContext();
        Assert.True(await db.Events.AnyAsync(e => e.WorkspaceId == workspaceId && e.Seq == seq, Ct));
    }

    /// <summary>
    /// The claim that makes the stream worth having: a client that was away does not lose what
    /// happened while it was. The two pages are compared against every event the store holds for the
    /// scope, so a cursor that repeated a row or skipped one fails here rather than in the UI.
    /// </summary>
    [Fact]
    public async Task A_client_reconnecting_with_its_last_seq_receives_the_gap_with_no_hole_and_no_repeat()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await StartAsync(world.Slug);
        var client = await server.SignInAsync(Ct);

        for (var i = 0; i < 4; i++)
        {
            await NoteAsync(world, $"before the operator looked away {i}");
        }

        var first = await AppServer.ReadJsonAsync(client, $"/api/v1/journal?scope={world.RunId}", Ct);
        var seen = Seqs(first);
        var cursor = first.GetProperty("cursor").GetInt64();

        Assert.Equal(4, seen.Count);
        Assert.Equal(seen.Order().ToList(), seen);
        Assert.Equal(seen[^1], cursor);
        Assert.False(first.GetProperty("hasMore").GetBoolean());

        for (var i = 0; i < 3; i++)
        {
            await NoteAsync(world, $"while the operator was away {i}");
        }

        // Paged deliberately: a descending page under a LIMIT would return the newest two and drop
        // the one between them, which is precisely the gap this cursor exists to close.
        var page = await AppServer.ReadJsonAsync(client, $"/api/v1/journal?scope={world.RunId}&since={cursor}&limit=2", Ct);
        var missed = Seqs(page);

        Assert.Equal(2, missed.Count);
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.All(missed, seq => Assert.True(seq > cursor, $"seq {seq} is not after the cursor {cursor}"));

        var rest = await AppServer.ReadJsonAsync(
            client, $"/api/v1/journal?scope={world.RunId}&since={page.GetProperty("cursor").GetInt64()}&limit=2", Ct);

        missed = [.. missed, .. Seqs(rest)];

        Assert.Equal(3, missed.Count);
        Assert.False(rest.GetProperty("hasMore").GetBoolean());

        // No hole and no repeat, stated as an exact set: everything the store holds for this run,
        // once each, in order.
        List<long> delivered = [.. seen, .. missed];
        Assert.Equal(await EverySeqAsync(world), delivered);

        // And asking again from the far end returns nothing rather than repeating the last row.
        var caughtUp = await AppServer.ReadJsonAsync(
            client, $"/api/v1/journal?scope={world.RunId}&since={missed[^1]}", Ct);

        Assert.Empty(Seqs(caughtUp));
        Assert.Equal(missed[^1], caughtUp.GetProperty("cursor").GetInt64());
    }

    private static List<long> Seqs(JsonElement journal)
        => [.. journal.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64())];

    private async Task<List<long>> EverySeqAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();

        return await db.Events
            .Where(e => e.WorkspaceId == world.WorkspaceId)
            .OrderBy(e => e.Seq)
            .Select(e => e.Seq)
            .ToListAsync(Ct);
    }

    /// <summary>A real write through the real writer, which is what the trigger fires on.</summary>
    private async Task<long> NoteAsync(WriteWorld world, string body)
    {
        var writer = new WorkWriter(
            fixture.DataSource,
            new BoardQueries(fixture.DataSource),
            new Housekeeping(fixture.DataSource));

        var result = await writer.NoteAsync(world.WorkspaceId, Author, "observation", body, "1.1", null, Ct);
        Assert.False(result.Refused, result.Text);

        await using var db = fixture.NewContext();
        return await db.Events.Where(e => e.WorkspaceId == world.WorkspaceId).MaxAsync(e => e.Seq, Ct);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        Channel<string> frames,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    frames.Writer.TryWrite(line[6..]);
                }
            }
        }
        catch (Exception)
        {
            // The response ends when the test disposes the server; nothing here is a failure the
            // test should report, because what it asserts is what arrived before then.
        }
        finally
        {
            frames.Writer.TryComplete();
        }
    }

    private static async Task<string?> NextAsync(
        Channel<string> frames,
        TimeSpan within,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(within);

        try
        {
            return await frames.Reader.ReadAsync(budget.Token);
        }
        catch (Exception failure) when (failure is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    private Task<AppServer> StartAsync(string slug)
        => AppServer.StartAsync(fixture.ConnectionString, slug, Ct);
}

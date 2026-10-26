using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using WorkState.Core.Queries;
using WorkState.Mcp;

namespace WorkState.Api.App;

/// <summary>What the trigger of migration <c>EvidenceAndJournal</c> puts on the wire.</summary>
internal sealed record StreamEvent(long Seq, string Kind, Guid Workspace, Guid? Item);

/// <summary>doc 10: an event means "re-read"; the ids say what to re-read. Additive over seq and kind.</summary>
internal sealed record StreamFrame(long Seq, Guid? RunId, Guid? ItemId, string Kind);

/// <summary><c>WorkState:Stream</c>. The heartbeat keeps an idle SSE connection open through a proxy.</summary>
public sealed class StreamOptions
{
    public const string Section = "WorkState:Stream";

    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// The live board, fed by the <c>pg_notify('workstate', …)</c> trigger that already exists on
/// <c>event</c> (doc 02 §11). One <c>LISTEN</c> per process on a connection of its own, fanned out
/// to every connected dashboard.
/// <para>
/// Nothing here polls. The board updates because the <em>database</em> changed, not because an
/// agent session is open — which is the whole reason doc 08 §1 chose a transport that outlives a
/// session. A client that misses events while disconnected does not lose them: every event carries
/// its <c>event.seq</c>, and <c>GET /api/v1/journal?scope={id}&amp;since=</c> returns the gap.
/// </para>
/// </summary>
public sealed class EventStream(NpgsqlDataSource dataSource, StreamOptions options, ILogger<EventStream> logger)
    : BackgroundService
{
    private const string Channel = "workstate";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One slow reader must not stall the others, and it must not silently miss events either: a
    /// subscriber that falls this far behind has its channel completed, which ends its response and
    /// sends it back through the journal with its last cursor.
    /// </summary>
    private const int SubscriberBacklog = 256;

    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web);

    private readonly TimeSpan _heartbeat = options.Heartbeat;

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    /// <summary>
    /// <see cref="Workspace"/> is null while the server is empty: the row is written by the first
    /// write, and that write's event is the one this subscriber is waiting for.
    /// </summary>
    private sealed record Subscriber(Guid? Workspace, Channel<StreamEvent> Channel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                // A dropped LISTEN is the one failure that is silent by nature: the dashboard keeps
                // rendering the board it already has and simply stops changing. Say so, then retry.
                logger.LogError(failure, "LISTEN {Channel} dropped; retrying in {Delay}.", Channel, RetryDelay);

                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        connection.Notification += OnNotification;

        try
        {
            await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
            {
                await listen.ExecuteNonQueryAsync(cancellationToken);
            }

            logger.LogInformation("Listening on {Channel} for journal events.", Channel);

            while (!cancellationToken.IsCancellationRequested)
            {
                await connection.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            connection.Notification -= OnNotification;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        if (Parse(args.Payload) is not { } message)
        {
            return;
        }

        foreach (var subscriber in _subscribers.Values)
        {
            if (subscriber.Workspace is { } workspace && workspace != message.Workspace)
            {
                continue;
            }

            if (!subscriber.Channel.Writer.TryWrite(message))
            {
                subscriber.Channel.Writer.TryComplete();
            }
        }
    }

    private StreamEvent? Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            return new StreamEvent(
                root.GetProperty("seq").GetInt64(),
                root.GetProperty("kind").GetString() ?? "unknown",
                Guid.Parse(root.GetProperty("ws").GetString()!),
                root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String
                    ? Guid.Parse(item.GetString()!)
                    : null);
        }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or FormatException)
        {
            logger.LogWarning(failure, "Unreadable {Channel} payload ignored.", Channel);
            return null;
        }
    }

    /// <summary>
    /// <c>GET /api/stream</c>. Server-Sent Events, under the read API's policy (doc 10 rule 5): the
    /// dashboard cookie, an agent token or a reader token — it carries ids and kinds, never content.
    /// </summary>
    internal static async Task HandleAsync(
        HttpContext context,
        EventStream stream,
        BoardQueries board,
        ApiQueries api,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        // An empty server streams too: a 503 here is what the board shows as "stream down" before
        // the first work_create, which is exactly when the operator is watching for it.
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // Proxies that buffer a response never deliver an event stream at all, and the failure looks
        // exactly like a server that has nothing to say.
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await context.Response.Body.FlushAsync(cancellationToken);

        var id = Guid.CreateVersion7();
        var channel = System.Threading.Channels.Channel.CreateBounded<StreamEvent>(
            new BoundedChannelOptions(SubscriberBacklog)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        stream._subscribers[id] = new Subscriber(workspaceId, channel);

        // Unresolved, the channel receives every workspace's events. The first one may be the write
        // that created ours: re-resolve on it, then narrow the subscription so the rest are filtered
        // where they are fanned out.
        async Task<bool> AdmitsAsync(StreamEvent message)
        {
            if (workspaceId is null
                && await board.WorkspaceAsync(workspace.Slug, cancellationToken) is { } resolved)
            {
                workspaceId = resolved;
                stream._subscribers[id] = new Subscriber(resolved, channel);
            }

            return message.Workspace == workspaceId;
        }

        try
        {
            async Task<Guid?> RunOfAsync(Guid? item)
                => item is { } id
                    ? (await api.PlacementsAsync([id], cancellationToken)).FirstOrDefault()?.RunId
                    : null;

            await PumpAsync(context, channel, stream._heartbeat, AdmitsAsync, RunOfAsync, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The operator closed the tab. Not a fault.
        }
        finally
        {
            stream._subscribers.TryRemove(id, out _);
        }
    }

    private static async Task PumpAsync(
        HttpContext context,
        Channel<StreamEvent> channel,
        TimeSpan heartbeat,
        Func<StreamEvent, Task<bool>> admits,
        Func<Guid?, Task<Guid?>> runOf,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var beat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            beat.CancelAfter(heartbeat);

            StreamEvent message;
            try
            {
                message = await channel.Reader.ReadAsync(beat.Token);
            }
            catch (ChannelClosedException)
            {
                // The subscriber fell behind and was dropped on purpose. Ending the response sends
                // it back with since=<its last seq>, which is exactly the recovery path.
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A comment, not an event: it keeps an idle connection open through a proxy without
                // the client having to distinguish it from something that happened.
                await context.Response.WriteAsync(": heartbeat\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            if (!await admits(message))
            {
                continue;
            }

            var frame = new StreamFrame(message.Seq, await runOf(message.Item), message.Item, message.Kind);

            await context.Response.WriteAsync(
                $"id: {message.Seq}\nevent: work\ndata: {JsonSerializer.Serialize(frame, FrameJson)}\n\n",
                cancellationToken);

            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
}

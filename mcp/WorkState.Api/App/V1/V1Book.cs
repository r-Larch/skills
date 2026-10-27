using System.Text.Json;
using WorkState.Core.Queries;

namespace WorkState.Api.App.V1;

/// <summary>
/// One request's lookups: every <see cref="ItemRefView"/>, <see cref="SessionView"/> and run
/// placement a response needs, loaded in batches and built in one place. doc 10 rule 2 lives here —
/// <see cref="ItemRefView.QualifiedRef"/> is <see cref="BoardQueries.CallableRefAsync"/>, the form
/// <c>work_get</c> prints, and no endpoint computes a ref of its own.
/// </summary>
internal sealed class V1Book(ApiQueries api, BoardQueries board)
{
    private readonly Dictionary<Guid, RefRow> _rows = [];
    private readonly Dictionary<Guid, ItemRefView> _refs = [];
    private readonly Dictionary<Guid, Placement> _placements = [];
    private readonly Dictionary<Guid, SessionView> _sessions = [];

    public async Task ItemsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var missing = ids.OfType<Guid>().Where(id => !_refs.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var row in await api.RefsAsync(missing, cancellationToken))
        {
            _rows[row.Id] = row;
            _refs[row.Id] = new ItemRefView(
                row.Id,
                row.Ref,
                await board.CallableRefAsync(row.Scope, row.RefShared, cancellationToken),
                row.Kind,
                row.Title,
                V1Text.Status(row.Status),
                row.FullRef);
        }
    }

    public Task ItemsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
        => ItemsAsync(ids.Select(id => (Guid?)id), cancellationToken);

    /// <summary>The items, their runs and projects, and the refs of those.</summary>
    public async Task PlacedAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        var missing = wanted.Where(id => !_placements.ContainsKey(id)).ToList();

        if (missing.Count > 0)
        {
            foreach (var placement in await api.PlacementsAsync(missing, cancellationToken))
            {
                _placements[placement.ItemId] = placement;
            }
        }

        await ItemsAsync(
            wanted.Concat(wanted.SelectMany(id => _placements.TryGetValue(id, out var p)
                ? new[] { p.RunId, p.ProjectId }.OfType<Guid>()
                : [])),
            cancellationToken);
    }

    public Task PlacedAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
        => PlacedAsync(ids.Select(id => (Guid?)id), cancellationToken);

    public async Task SessionsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var missing = ids.OfType<Guid>().Where(id => !_sessions.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var row in await api.SessionsByIdAsync(missing, cancellationToken))
        {
            _sessions[row.Id] = Session(row);
        }
    }

    public static SessionView Session(SessionRow row)
        => new(
            row.Id,
            row.Name,
            V1Text.Snake(row.Role),
            row.ExternalRole,
            row.Origin,
            row.LaunchedBy,
            row.ClaudeSessionId,
            row.Cwd,
            row.Tree,
            row.ClientName is { Length: > 0 } name
                ? row.ClientVersion is { Length: > 0 } version ? $"{name} {version}" : name
                : null,
            row.FirstSeenAt,
            row.LastSeenAt);

    public ItemRefView Item(Guid id) => _refs[id];

    public ItemRefView? Item(Guid? id) => id is { } value && _refs.TryGetValue(value, out var found) ? found : null;

    public RefRow Row(Guid id) => _rows[id];

    public bool Has(Guid id) => _refs.ContainsKey(id);

    public Guid? RunId(Guid id) => _placements.TryGetValue(id, out var p) ? p.RunId : null;

    public Guid? ProjectId(Guid id) => _placements.TryGetValue(id, out var p) ? p.ProjectId : null;

    public ItemRefView? RunOf(Guid id) => Item(RunId(id));

    public SessionView? Session(Guid? id) => id is { } value && _sessions.TryGetValue(value, out var found) ? found : null;

    public async Task<IReadOnlyList<ClaimView>> ClaimsAsync(
        IReadOnlyList<ClaimDetail> claims,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        await PlacedAsync(claims.Select(c => c.ItemId), cancellationToken);
        await SessionsAsync(claims.Select(c => c.SessionId), cancellationToken);

        return [.. claims.Select(c => Claim(c, asOf))];
    }

    /// <summary>Only after <see cref="ClaimsAsync"/> or the equivalent loads.</summary>
    public ClaimView Claim(ClaimDetail claim, DateTimeOffset asOf)
    {
        var session = Session(claim.SessionId);

        return new(
            Item(claim.ItemId),
            RunOf(claim.ItemId),
            session,
            claim.AgentName,
            V1Text.Snake(claim.Role),
            claim.Tree,
            claim.AcquiredAt,
            claim.HeartbeatAt,
            claim.ExpiresAt,
            claim.ExpiresAt < asOf,
            session?.Name);
    }

    public async Task<IReadOnlyList<JournalEventView>> EventsAsync(
        IReadOnlyList<EventDetail> events,
        CancellationToken cancellationToken)
    {
        await PlacedAsync(events.Select(e => e.ItemId), cancellationToken);
        await SessionsAsync(events.Select(e => e.SessionId), cancellationToken);

        return [.. events.Select(e => new JournalEventView(
            e.Seq,
            e.At,
            Item(e.ItemId),
            e.ItemId is { } item ? RunOf(item) : null,
            Session(e.SessionId),
            e.Actor,
            V1Text.Snake(e.Role),
            e.Kind,
            e.Summary,
            Json(e.Payload) ?? Json("{}")!.Value))];
    }

    public static JsonElement? Json(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

namespace WorkState.Api.App;

/// <summary>
/// The JSON the dashboard consumes. These records are a contract with the SPA unit, which is why
/// they are declared here rather than returned as anonymous objects: an anonymous shape is a
/// contract nobody can find, and the next unit is written against exactly these names.
/// <para>
/// Every instant is sent as an instant. Nothing here is a "seconds remaining" — that number is
/// stale the moment it is serialised, and the two places it would be wrong are a lease countdown
/// and a journal timestamp, which are the two things on the board a human reads for urgency.
/// </para>
/// </summary>
public sealed record ProjectView(string Ref, string Title, string Status, int RunCount);

/// <summary>doc 09 §5's WORKING NOW row. A lapsed lease is an <see cref="ExpiresAt"/> in the past,
/// not a separate kind of row: the client draws the same line with the clock past zero.</summary>
public sealed record LeaseView(
    string Agent,
    string Ref,
    string QualifiedRef,
    string Title,
    DateTimeOffset ExpiresAt);

/// <summary>doc 09 §5's NEEDS YOU row. <see cref="Call"/> is the MCP call that resolves it and is
/// never empty — the dashboard shows the operator the same route the agent is given.</summary>
public sealed record AttentionView(string Rank, string? Ref, string What, string Call);

/// <summary>
/// The honesty strip of doc 09 §5 — one line, three counts, and the reason the product exists.
/// Not a chart: a count that should be zero and usually is not.
/// </summary>
public sealed record HonestyView(int DoneUnverified, int Forced, int Ungated);

/// <summary>
/// <see cref="AsOf"/> is the server's clock at the moment the board was read. The client needs it
/// to render a lease countdown that is correct on a machine whose clock differs from the server's,
/// which is every machine.
/// </summary>
public sealed record WatchView(
    string? Project,
    DateTimeOffset AsOf,
    IReadOnlyList<LeaseView> WorkingNow,
    IReadOnlyList<AttentionView> NeedsYou,
    HonestyView Honesty);

/// <summary>
/// One node of the run tree. <see cref="Status"/> and the rollup are both present, and
/// <see cref="Disagrees"/> is the pair read together: a container the store calls closed while its
/// children are not. doc 09 §5 — <c>phase-4</c> is <c>done</c> with 10 of 23 and <c>phase-6</c>
/// with 0 of 9. Sending the status alone would launder the one thing this product exists to show.
/// </summary>
public sealed record TreeNodeView(
    string Ref,
    string Kind,
    string Title,
    string Status,
    int Depth,
    int DoneCount,
    int TotalCount,
    bool Disagrees);

public sealed record TreeView(string Ref, string Title, IReadOnlyList<TreeNodeView> Nodes);

public sealed record JournalEntryView(
    long Seq,
    string? Ref,
    DateTimeOffset At,
    string Actor,
    string Role,
    string Kind,
    string? Summary);

/// <summary>
/// A contiguous page of the journal, <b>ascending by seq</b>, starting immediately after the
/// requested cursor. Ascending is the contract, not a preference: a descending page under a
/// <c>LIMIT</c> returns the newest events after the cursor and drops the ones in between, so a
/// client reconnecting with its last seq would lose precisely what it reconnected to collect.
/// <para>
/// <see cref="Cursor"/> is the seq to pass as <c>since</c> next time. <see cref="HasMore"/> says
/// the page was truncated, so a client that has been away a long time knows to ask again rather
/// than assume it has caught up.
/// </para>
/// </summary>
public sealed record JournalView(long Cursor, bool HasMore, IReadOnlyList<JournalEntryView> Events);

public sealed record SessionRequest(string? Username, string? Password);

using System.Text.Json;

namespace WorkState.Api.App.V1;

// doc 10 — the frozen v1 shapes, one record per object in the spec. Field names are the spec's,
// camelCased by the host's web JSON defaults. Additive only: never rename or remove a member here.

public sealed record ItemRefView(Guid Id, string Ref, string QualifiedRef, string Kind, string Title, string Status);

public sealed record RollupView(
    int Total,
    int Done,
    int InProgress,
    int Review,
    int Blocked,
    int Open,
    int Cancelled,
    int Superseded);

public sealed record SessionView(
    Guid Id,
    string Name,
    string Role,
    string? ExternalRole,
    string? Origin,
    string? LaunchedBy,
    string? ClaudeSessionId,
    string? Cwd,
    string? Tree,
    string? Client,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record ClaimView(
    ItemRefView Item,
    ItemRefView? Run,
    SessionView? Session,
    string Agent,
    string Role,
    string? Tree,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    bool Expired);

public sealed record JournalEventView(
    long Seq,
    DateTimeOffset At,
    ItemRefView? Item,
    ItemRefView? Run,
    SessionView? Session,
    string Actor,
    string Role,
    string Kind,
    string? Summary,
    JsonElement Payload);

public sealed record LastVerificationView(bool Passed, DateTimeOffset At);

public sealed record RunOverviewView(
    ItemRefView Item,
    RollupView Rollup,
    string? Branch,
    string? Worktree,
    int ActiveClaims,
    DateTimeOffset? LastEventAt,
    int InboxCount,
    LastVerificationView? LastVerification);

public sealed record RepoView(string Kind, string Key);

public sealed record ProjectOverviewView(
    ItemRefView Item,
    RollupView Rollup,
    IReadOnlyList<RunOverviewView> Runs,
    IReadOnlyList<RepoView> Repos);

public sealed record OverviewView(
    DateTimeOffset AsOf,
    long Seq,
    IReadOnlyList<ProjectOverviewView> Projects,
    IReadOnlyList<ClaimView> WorkingNow,
    int InboxCount,
    HonestyView Honesty);

public sealed record CriteriaCountView(int Checked, int Total);

public sealed record VerificationSummaryView(bool Passed, string RunBy, DateTimeOffset At);

public sealed record BoardRowView(
    ItemRefView Item,
    Guid? ParentId,
    int Depth,
    string Seq,
    bool HasChildren,
    string? Resolution,
    short Priority,
    string? Severity,
    CriteriaCountView Criteria,
    RollupView? Rollup,
    ClaimView? Claim,
    string? Commit,
    VerificationSummaryView? Verification,
    IReadOnlyList<ItemRefView> BlockedBy,
    IReadOnlyList<string> Flags,
    DateTimeOffset OpenedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ClosedAt);

public sealed record BoardView(
    DateTimeOffset AsOf,
    long Seq,
    ItemRefView Run,
    ItemRefView? Project,
    string? Intent,
    string? Branch,
    string? Worktree,
    RollupView Rollup,
    IReadOnlyList<BoardRowView> Rows);

public sealed record CriterionView(string Id, string Text, bool Checked, string? Evidence, DateTimeOffset? CheckedAt);

public sealed record ReportView(
    int Attempt,
    SessionView? Session,
    string? Agent,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    IReadOnlyList<string> Files,
    string? Verify,
    string? RedCheck,
    string? Deviations,
    string? Risks,
    string? NotDone,
    DateTimeOffset At);

public sealed record VerificationView(
    string? Command,
    string ResultLine,
    bool Passed,
    JsonElement? Counts,
    string RunBy,
    SessionView? Session,
    string? RedCheck,
    DateTimeOffset At);

public sealed record ArtifactView(string Kind, string Value, DateTimeOffset At);

public sealed record ChildView(ItemRefView Item, CriteriaCountView Criteria);

public sealed record FindingView(ItemRefView Item, string? Severity, string? Source);

public sealed record NoteView(
    Guid Id,
    string Kind,
    string Body,
    ItemRefView? From,
    ItemRefView? To,
    DateTimeOffset? ConsumedAt,
    DateTimeOffset CreatedAt,
    SessionView? Session);

public sealed record LinkView(string Type, ItemRefView Item);

public sealed record LinksView(IReadOnlyList<LinkView> Out, IReadOnlyList<LinkView> In);

public sealed record AssumptionView(string Ref, string Text, string Status, ItemRefView? RevisitWhen);

public sealed record ItemPageView(
    DateTimeOffset AsOf,
    long Seq,
    ItemRefView Item,
    IReadOnlyList<ItemRefView> Path,
    string? Intent,
    string? VerifyCmd,
    IReadOnlyList<string> RiskTags,
    short Priority,
    string? Severity,
    string? Source,
    string? Resolution,
    string? ClosedBy,
    DateTimeOffset OpenedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<CriterionView> Criteria,
    ClaimView? Claim,
    IReadOnlyList<ReportView> Reports,
    IReadOnlyList<VerificationView> Verifications,
    IReadOnlyList<ArtifactView> Artifacts,
    IReadOnlyList<ChildView> Children,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyList<NoteView> Notes,
    LinksView Links,
    IReadOnlyList<AssumptionView> Assumptions,
    IReadOnlyList<JournalEventView> Events,
    bool EventsHasMore);

public sealed record InboxRowView(
    string Key,
    string Kind,
    ItemRefView Item,
    ItemRefView? Run,
    string What,
    DateTimeOffset Since,
    string Call);

public sealed record InboxView(DateTimeOffset AsOf, long Seq, IReadOnlyList<InboxRowView> Rows);

public sealed record JournalPageView(
    DateTimeOffset AsOf,
    long Seq,
    long Cursor,
    bool HasMore,
    IReadOnlyList<JournalEventView> Events);

public sealed record BacklogGroupView(ItemRefView? Origin, IReadOnlyList<ItemRefView> Items);

public sealed record BacklogView(DateTimeOffset AsOf, long Seq, ItemRefView? Run, IReadOnlyList<BacklogGroupView> Groups);

public sealed record SessionHoldingView(SessionView Session, IReadOnlyList<ClaimView> Claims);

public sealed record SessionsView(DateTimeOffset AsOf, long Seq, IReadOnlyList<SessionHoldingView> Sessions);

public sealed record ResolveView(DateTimeOffset AsOf, long Seq, IReadOnlyList<ItemRefView> Matches);

using WorkState.Core.Domain;

namespace WorkState.Core.Queries;

/// <summary>
/// The rows the JSON read API (doc 10) is built from. Keyed by id throughout: a ref is a label, and
/// the API's first rule is that nothing is addressed by one.
/// </summary>
public sealed record RefRow(
    Guid Id,
    Guid WorkspaceId,
    string Ref,
    string Kind,
    string Title,
    string? Intent,
    ItemStatus Status,
    Guid? RefScopeId,
    Guid? ParentId,
    string? QualifiedRef,
    bool RefShared,
    DateTimeOffset OpenedAt,
    string FullRef)
{
    public ScopeRef Scope => new(Id, WorkspaceId, Ref, Kind, Title, Intent, Status, RefScopeId, ParentId, QualifiedRef);
}

/// <summary>The nearest run and project at or above an item.</summary>
public sealed record Placement(Guid ItemId, Guid? RunId, Guid? ProjectId);

/// <summary>doc 10's <c>Rollup</c>: leaf kinds only, every status including the two that close undone.</summary>
public sealed record LeafRollup(
    Guid Id,
    int Open,
    int InProgress,
    int Blocked,
    int Review,
    int Done,
    int Cancelled,
    int Superseded)
{
    public int Total => Open + InProgress + Blocked + Review + Done + Cancelled + Superseded;
}

public sealed record ClaimDetail(
    Guid Id,
    Guid ItemId,
    Guid? SessionId,
    string AgentName,
    ActorRole Role,
    string? Tree,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReleasedAt);

public sealed record SessionRow(
    Guid Id,
    string Name,
    ActorRole Role,
    string? ExternalRole,
    string? Origin,
    string? LaunchedBy,
    string? ClaudeSessionId,
    string? Cwd,
    string? Tree,
    string? ClientName,
    string? ClientVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record SessionTokenRow(Guid Id, string Token);

public sealed record RunRow(
    Guid Id,
    string? Branch,
    string? Worktree,
    int ActiveClaims,
    DateTimeOffset? LastEventAt);

/// <summary>
/// One row of a run board. <see cref="RollupTotal"/>/<see cref="RollupDone"/> are the
/// <c>item_rollup</c> numbers the pre-v1 tree judged a disagreement by, kept so the flag means the same.
/// </summary>
public sealed record BoardRow(
    Guid Id,
    Guid? ParentId,
    int Depth,
    decimal Seq,
    string Kind,
    ItemStatus Status,
    ItemResolution? Resolution,
    short Priority,
    string? Severity,
    DateTimeOffset OpenedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ClosedAt,
    bool HasChildren,
    int CriteriaTotal,
    int CriteriaChecked,
    string? Commit,
    bool? VerificationPassed,
    VerifyRunner? VerificationRunBy,
    DateTimeOffset? VerificationAt,
    bool RequiresVerify,
    bool OrchestratorVerified,
    bool Forced,
    bool UnconsumedNotes,
    int RollupTotal,
    int RollupDone);

public sealed record ItemDetailRow(
    string? Intent,
    string? VerifyCmd,
    string RiskTags,
    short Priority,
    string? Severity,
    string? Source,
    ItemResolution? Resolution,
    string? ClosedBy,
    DateTimeOffset OpenedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ClosedAt);

public sealed record CriterionDetail(int Seq, string Text, bool Checked, string? Evidence, DateTimeOffset? CheckedAt);

public sealed record ReportRow(
    int Attempt,
    string? AgentName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    string Files,
    string? Deviations,
    string? Risks,
    string? NotDone,
    string? Raw,
    DateTimeOffset At,
    Guid? SessionId);

public sealed record VerificationDetail(
    string? Command,
    string ResultLine,
    bool Passed,
    string? Counts,
    VerifyRunner RunBy,
    Guid? SessionId,
    string? RedCheck,
    DateTimeOffset At);

public sealed record ArtifactRow(string Kind, string Value, DateTimeOffset At);

public sealed record ChildRow(Guid Id, int CriteriaTotal, int CriteriaChecked);

public sealed record FindingRow(Guid Id, string? Severity, string? Source);

public sealed record NoteDetail(
    Guid Id,
    NoteKind Kind,
    string Body,
    Guid? FromItemId,
    Guid? ToItemId,
    DateTimeOffset? ConsumedAt,
    DateTimeOffset CreatedAt,
    Guid? SessionId);

public sealed record AssumptionDetail(string Ref, string Statement, AssumptionState State, Guid? RevisitItemId);

public sealed record EventDetail(
    long Seq,
    Guid? ItemId,
    DateTimeOffset At,
    string Actor,
    ActorRole Role,
    string Kind,
    string? Summary,
    string Payload,
    Guid? SessionId);

/// <summary>An inbox candidate the attention queries do not already produce.</summary>
public sealed record WaitingRow(Guid Id, DateTimeOffset Since);

public sealed record BacklogRow(Guid Id, Guid? OriginId);

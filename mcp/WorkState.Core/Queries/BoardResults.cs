using WorkState.Core.Domain;

namespace WorkState.Core.Queries;

/// <summary>
/// The read side the renderers consume. Every record here is the shape of one rendered block, not a
/// row of a table: these exist because <see cref="WorkStateQueries"/> answers "what is actionable"
/// and "what is unclosed", and <c>work_brief</c> additionally needs the constraints, the notes and
/// the last piece of evidence in scope.
/// </summary>
public sealed record ScopeRef(
    Guid Id,
    Guid WorkspaceId,
    string Ref,
    string Kind,
    string Title,
    string? Intent,
    ItemStatus Status,
    Guid? RefScopeId,
    Guid? ParentId,
    string? QualifiedRef);

public sealed record ScopeHeader(
    string Kind,
    string Ref,
    string? RunRef,
    string? ProjectRef,
    string? Branch,
    string? Head,
    string? Goal);

public sealed record ConstraintRow(ConstraintKind Kind, string Text, string? Route, string Severity);

public sealed record AssumptionRow(
    string Ref,
    string Statement,
    EvidenceLevel Evidence,
    string? EvidenceNote,
    ReversalCost? Reversal,
    AssumptionState State,
    string? RevisitRef,
    ItemStatus? RevisitStatus,
    DateTimeOffset? RevisitClosedAt);

public sealed record NoteRow(NoteKind Kind, string Body, string? ToRef, string? FromRef);

public sealed record VerificationRow(
    string ItemRef,
    string? Command,
    string ResultLine,
    bool Passed,
    VerifyRunner RunBy,
    DateTimeOffset At);

/// <summary>Per-item evidence and shape, keyed by ref, for the flag vocabulary of doc 06 §2.</summary>
public sealed record ItemFacts(
    string Ref,
    int CriteriaTotal,
    int CriteriaChecked,
    int VerificationsPassed,
    int OrchestratorVerifications,
    string? Commit,
    long OpenChildren,
    bool ReviewRequired,
    bool RequiresVerify,
    string? Severity,
    string? ParentRef,
    DateTimeOffset? StartedAt);

public sealed record BlockerRow(string FindingRef, string FindingTitle, string? TargetRef);

public sealed record BlockedRow(string Ref, string BlockedByRef);

public sealed record ClaimRow(string ItemRef, string AgentName, DateTimeOffset ExpiresAt);

/// <summary>
/// A lease the watch floor shows (doc 09 §5): the claim itself, plus the title and qualified ref a
/// human needs to know what is being worked on. <see cref="ExpiresAt"/> is the instant, never a
/// remaining duration — the client counts down against it, and a number computed here is wrong by
/// the time it is read.
/// </summary>
public sealed record LiveClaimRow(
    string ItemRef,
    string QualifiedRef,
    string Title,
    string AgentName,
    DateTimeOffset ExpiresAt);

public sealed record StaleRow(string Ref, DateTimeOffset StartedAt);

/// <summary>One entry of the dashboard's project switcher (doc 09 §5), with the runs under it.</summary>
public sealed record ProjectRow(Guid Id, string Ref, string Title, ItemStatus Status, int RunCount);

/// <summary>A phase with nothing left to do and no gate recorded against it (doc 07 §2).</summary>
public sealed record GateRow(string PhaseRef, int TaskCount);

public sealed record EventRow(
    long Seq,
    string? ItemRef,
    DateTimeOffset At,
    string Actor,
    ActorRole Role,
    string Kind,
    string? Summary);

public sealed record CriterionRow(string ItemRef, int Seq, string Text, bool Checked, string? Evidence);

public sealed record LinkRow(string FromRef, string ToRef, LinkType Type, ItemStatus FromStatus);

public sealed record AuditRow(string Check, string Ref, string Problem, string Route);

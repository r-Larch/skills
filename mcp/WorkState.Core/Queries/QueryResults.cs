using WorkState.Core.Domain;

namespace WorkState.Core.Queries;

/// <summary>A row of the ready frontier: work that is actionable right now, in the order to take it.</summary>
public sealed record ReadyItem(
    Guid Id,
    string Ref,
    string Kind,
    string Title,
    string? VerifyCmd,
    string[] RiskTags,
    short Priority,
    decimal Seq);

/// <summary>
/// An edge pointing at work that will never arrive: a blocker that was abandoned, an
/// uncommissioned stub, or an ordering inversion (plan-audit check #7).
/// </summary>
public sealed record UnclosedDependency(
    string Waiting,
    string On,
    ItemStatus Status,
    bool OrderedCorrectly);

/// <summary>
/// The four unclosed buckets plus <see cref="DoneCount"/>. <c>cancelled</c> and <c>superseded</c>
/// are in none of them — they are closed and were not done, so they belong in neither half of an
/// "X of Y" — but <see cref="ReviewCount"/> is work in flight and belongs in the denominator.
/// </summary>
public sealed record ItemRollup(
    Guid Id,
    long OpenCount,
    long DoingCount,
    long BlockedCount,
    long ReviewCount,
    long DoneCount);

/// <summary>An item in document order, with the rollup of everything beneath it.</summary>
public sealed record SubtreeNode(
    Guid Id,
    Guid? ParentId,
    string Ref,
    string Kind,
    string Title,
    ItemStatus Status,
    int Depth,
    decimal Seq,
    long OpenCount,
    long DoingCount,
    long BlockedCount,
    long ReviewCount,
    long DoneCount);

/// <summary>Everything unclosed that is not a task — the work_threads backbone.</summary>
public sealed record OpenThread(string Type, string Ref, string Text, Guid? Scope);

public sealed record SearchHit(string Source, Guid Id, string Ref, string Title, string Snippet, float Rank);

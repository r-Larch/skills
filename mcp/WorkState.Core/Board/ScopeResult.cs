using WorkState.Core.Queries;

namespace WorkState.Core.Board;

/// <summary>
/// What a <c>scope</c> argument turned into. The failures are values rather than exceptions because
/// each one has a different route out, and doc 04 §3 makes naming that route the whole job of an
/// error message.
/// </summary>
public abstract record ScopeResult
{
    public sealed record Found(Guid WorkspaceId, ScopeRef Item) : ScopeResult;

    /// <summary>No workspace, or a workspace with nothing in it. The cold start.</summary>
    public sealed record Empty : ScopeResult;

    public sealed record Missing(string Asked, IReadOnlyList<string> Nearby) : ScopeResult;

    public sealed record Ambiguous(string Asked, IReadOnlyList<string> Candidates) : ScopeResult;
}

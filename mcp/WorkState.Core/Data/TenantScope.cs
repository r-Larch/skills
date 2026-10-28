namespace WorkState.Core.Data;

/// <summary>
/// The workspace a tenant connection (<see cref="WorkStateConnections.TenantAsync"/>) acts in, for the rest of
/// the async flow that entered it. The API enters it once per request from the principal, after authorization
/// (<c>ApiHost</c>); a path that authorized a tenant of its own enters that one around the call (the
/// <c>/r/</c> links). Inside a scope with no workspace, and outside every scope, a tenant connection sees no
/// tenant's rows (doc 02 §15).
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<Frame?> Current = new();

    public static IDisposable Enter(Guid? workspaceId)
    {
        var outer = Current.Value;
        Current.Value = new Frame(workspaceId);
        return new Exit(outer);
    }

    /// <summary>False outside every scope; inside one, its workspace, null when the principal has none.</summary>
    public static bool TryGet(out Guid? workspaceId)
    {
        workspaceId = Current.Value?.WorkspaceId;
        return Current.Value is not null;
    }

    private sealed record Frame(Guid? WorkspaceId);

    private sealed class Exit(Frame? outer) : IDisposable
    {
        public void Dispose() => Current.Value = outer;
    }
}

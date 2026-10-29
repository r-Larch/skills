namespace WorkState.Core.Data;

/// <summary>
/// The workspace a tenant connection (<see cref="WorkStateConnections.TenantAsync"/>) acts in, and the user it
/// acts for, for the rest of the async flow that entered it. The API enters it once per request from the
/// principal, after authorization (<c>ApiHost</c>); a path that authorized a tenant of its own enters that one
/// around the call (the <c>/r/</c> links). Inside a scope with no workspace, and outside every scope, a tenant
/// connection sees no tenant's rows (doc 02 §15). Inside a scope with a user it sees only the projects that user
/// may see; with none — a static token — every project of its workspace (doc 02 §15b).
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<Frame?> Current = new();

    /// <summary>A workspace and no user: every project of it, as for a static token.</summary>
    public static IDisposable Enter(Guid? workspaceId) => EnterAs(workspaceId, null);

    /// <summary>A workspace and the user the request acts for, null for a credential that has none.</summary>
    public static IDisposable EnterAs(Guid? workspaceId, Guid? userId)
    {
        var outer = Current.Value;
        Current.Value = new Frame(workspaceId, userId);
        return new Exit(outer);
    }

    /// <summary>False outside every scope; inside one, its workspace, null when the principal has none.</summary>
    public static bool TryGet(out Guid? workspaceId) => TryGet(out workspaceId, out _);

    /// <summary>As <see cref="TryGet(out Guid?)"/>, and the scope's user, null for a credential that has none.</summary>
    public static bool TryGet(out Guid? workspaceId, out Guid? userId)
    {
        workspaceId = Current.Value?.WorkspaceId;
        userId = Current.Value?.UserId;
        return Current.Value is not null;
    }

    private sealed record Frame(Guid? WorkspaceId, Guid? UserId);

    private sealed class Exit(Frame? outer) : IDisposable
    {
        public void Dispose() => Current.Value = outer;
    }
}

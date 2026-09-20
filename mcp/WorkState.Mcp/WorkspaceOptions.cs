namespace WorkState.Mcp;

/// <summary>
/// Which workspace this server serves. One row today (doc 02 §2); a null slug means "the only one
/// there is", which is what a freshly migrated database has and what the tests rely on.
/// </summary>
public sealed class WorkspaceOptions
{
    public string? Slug { get; set; }
}

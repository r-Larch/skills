using NpgsqlTypes;

namespace WorkState.Core.Domain;

/// <summary>One row today; the seam if multi-user ever lands (doc 02 §2).</summary>
public class Workspace
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "default";
    public string Settings { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public class ItemKind
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Rank { get; set; }
    public string[] AllowedParentKinds { get; set; } = [];
    public bool ScopesRefs { get; set; }
    public bool RequiresVerify { get; set; }
    public bool RequiresCriteria { get; set; }
    public bool RequiresCommit { get; set; }
    public bool IsGateable { get; set; }
}

public class Item
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ParentId { get; set; }
    public string Kind { get; set; } = "task";
    public string Ref { get; set; } = "";

    /// <summary>Nearest ancestor whose kind has <c>scopes_refs</c>, or self. Trigger-maintained.</summary>
    public Guid? RefScopeId { get; set; }

    public decimal Seq { get; set; }

    /// <summary>root..parent. Trigger-maintained on insert and on reparent.</summary>
    public Guid[] Ancestors { get; set; } = [];

    public int Depth { get; private set; }

    public string Title { get; set; } = "";
    public string? Intent { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Open;
    public ItemResolution? Resolution { get; set; }
    public short Priority { get; set; }

    public string? VerifyCmd { get; set; }
    public string[] RiskTags { get; set; } = [];
    public bool ReviewRequired { get; private set; }
    public string? GateCriterion { get; set; }

    public string? Severity { get; set; }
    public string? Source { get; set; }

    public Guid? BodyDocId { get; set; }
    public int Revision { get; set; } = 1;

    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }

    public NpgsqlTsVector? Search { get; private set; }
}

public class ProjectConfig
{
    public Guid ItemId { get; set; }
    public string RepoRoot { get; set; } = "";
    public string DefaultBranch { get; set; } = "master";
    public string VerifyCatalog { get; set; } = "{}";
}

public class RunConfig
{
    public Guid ItemId { get; set; }
    public Guid ProjectItemId { get; set; }
    public string? Branch { get; set; }
    public string? WorktreePath { get; set; }

    /// <summary>The write lock: defaults to <c>worktree_path</c>, else the project's repo root.</summary>
    public string SerializationKey { get; set; } = "";

    public string Mode { get; set; } = "full";
    public string? OwnerAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ItemCriterion
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public int Seq { get; set; }
    public string Text { get; set; } = "";
    public bool Checked { get; set; }
    public string? Evidence { get; set; }
    public Guid? EvidenceVerificationId { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}

public class ItemLink
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid FromItemId { get; set; }
    public Guid ToItemId { get; set; }
    public LinkType Type { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

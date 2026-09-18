namespace WorkState.Core.Domain;

public class Assumption
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Null means the whole workspace.</summary>
    public Guid? ScopeItemId { get; set; }

    /// <summary>
    /// The run or project the ref is numbered within, derived from <see cref="ScopeItemId"/> by the
    /// same rule as <see cref="Item.RefScopeId"/>. Without it, parallel orchestrators would be
    /// fighting over a single global 'A1'.
    /// </summary>
    public Guid? RefScopeId { get; set; }

    public string Ref { get; set; } = "";
    public string Statement { get; set; } = "";
    public EvidenceLevel Evidence { get; set; }
    public ReversalCost Reversal { get; set; }

    /// <summary>"Revisit when: task 1.1" as a real edge, not a sentence nobody re-reads.</summary>
    public Guid? RevisitItemId { get; set; }

    public DateTimeOffset? RevisitAt { get; set; }
    public AssumptionState State { get; set; } = AssumptionState.Open;
    public string? ResolutionNote { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ConstraintRule
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ScopeItemId { get; set; }
    public ConstraintKind Kind { get; set; }
    public string Text { get; set; } = "";

    /// <summary>The way out. A prohibition with no route is a trap the author set themselves.</summary>
    public string? Route { get; set; }

    public string[] CoversRisk { get; set; } = [];
    public string Severity { get; set; } = "binding";
    public bool Active { get; set; } = true;
    public Guid? AddedByItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Note
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? FromItemId { get; set; }
    public NoteKind Kind { get; set; }
    public string Body { get; set; } = "";

    public Guid? ToItemId { get; set; }
    public string? ToSelector { get; set; }

    /// <summary>A correction supersedes what it corrects; it does not append 500 lines away.</summary>
    public Guid? CorrectsNoteId { get; set; }

    public Guid? CorrectsBlockId { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? ConsumedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class Claim
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }

    /// <summary>Copied from the owning run_config at claim time; the write lock is on the tree.</summary>
    public string SerializationKey { get; set; } = "";

    /// <summary>Read-only recon claims do not serialize.</summary>
    public bool Writes { get; set; } = true;

    public string AgentName { get; set; } = "";
    public Guid? AgentTokenId { get; set; }
    public ActorRole Role { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

public class AgentToken
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ScopeItemId { get; set; }
    public ActorRole Role { get; set; }
    public string AgentName { get; set; } = "";

    /// <summary>SHA-256. The plaintext is returned once and never stored.</summary>
    public byte[] TokenHash { get; set; } = [];

    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

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

    /// <summary>
    /// The provenance beside the level — "confirmed: the key is in configuration" (doc 02 §7,
    /// import finding #3). The enum keeps only the level, which is not the part a later reader needs.
    /// </summary>
    public string? EvidenceNote { get; set; }

    /// <summary>Null once the assumption is resolved: a settled question has no reversal cost.</summary>
    public ReversalCost? Reversal { get; set; }

    /// <summary>"Revisit when: task 1.1" as a real edge, not a sentence nobody re-reads.</summary>
    public Guid? RevisitItemId { get; set; }

    public DateTimeOffset? RevisitAt { get; set; }
    public AssumptionState State { get; set; } = AssumptionState.Open;
    public string? ResolutionNote { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? SessionId { get; set; }
}

public class ConstraintRule
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ScopeItemId { get; set; }

    /// <summary>
    /// <c>C1</c>, <c>C2</c>, … numbered within the workspace rather than within a run, because one
    /// BINDING block mixes workspace-wide rules with rules scoped to the item's ancestors: a
    /// per-run number would print two <c>C1</c>s in the same block, and a ref that is ambiguous
    /// where it is read cannot be cited.
    /// </summary>
    public string Ref { get; set; } = "";

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
    public Guid? SessionId { get; set; }
}

public class Claim
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }

    /// <summary>
    /// The working tree the write lock is on: the <c>tree</c> the caller named, else the owning
    /// run_config's key, else the project's repo root, else the ref scope.
    /// </summary>
    public string SerializationKey { get; set; } = "";

    /// <summary>
    /// The workspace the lock is held in: every key, a path included, locks within its tenant. Null only
    /// on a claim released before that rule (the ClaimKeyWorkspace migration filled the live ones).
    /// </summary>
    public Guid? KeyWorkspaceId { get; set; }

    /// <summary>
    /// Whose machine a path key is on: a path names a folder on one person's machine, so it locks per user. Null for a
    /// name key (a branch, the ref scope), which locks per workspace, and for a path claimed by a credential with no user.
    /// </summary>
    public Guid? KeyUserId { get; set; }

    /// <summary>The normalised <c>tree</c> the caller passed to <c>work_status</c>, if it passed one.</summary>
    public string? Tree { get; set; }

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

    /// <summary>
    /// The holder. One session may hold several items of one tree; a second session may not —
    /// <c>claim_one_session_per_tree</c>. A null session is a claim from before doc 11, and still
    /// holds its tree alone (<c>claim_one_writer_per_tree</c>).
    /// </summary>
    public Guid? SessionId { get; set; }
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

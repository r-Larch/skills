namespace WorkState.Core.Domain;

public class Attempt
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public int N { get; set; }
    public string? AgentName { get; set; }
    public string? AgentKind { get; set; }

    /// <summary>The .jsonl transcript handle — a pointer, never the transcript (doc 00 §7).</summary>
    public string? SessionId { get; set; }

    /// <summary>The brief AS ISSUED; block revisions make "what did it say then" answerable.</summary>
    public Guid? BriefDocId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Outcome { get; set; }
}

public class Report
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public string Status { get; set; } = "";
    public string Files { get; set; } = "[]";
    public string? Deviations { get; set; }
    public string? Risks { get; set; }
    public string? NotDone { get; set; }
    public string? Raw { get; set; }
    public DateTimeOffset At { get; set; }
}

public class Verification
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ItemId { get; set; }
    public Guid? AttemptId { get; set; }
    public string Command { get; set; } = "";

    /// <summary>"Passed! 187/187" — the actual line, not an exit code. Required, by decision.</summary>
    public string ResultLine { get; set; } = "";

    public bool Passed { get; set; }
    public string? Counts { get; set; }
    public string? Samples { get; set; }
    public VerifyRunner RunBy { get; set; }
    public string? RedCheck { get; set; }
    public DateTimeOffset At { get; set; }
}

public class Artifact
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Meta { get; set; } = "{}";
    public DateTimeOffset At { get; set; }
}

public class Event
{
    /// <summary>The global cursor everything pages by: sync, SSE, and work_history(since_seq).</summary>
    public long Seq { get; set; }

    public Guid WorkspaceId { get; set; }
    public Guid? ItemId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public ActorRole Role { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? Summary { get; set; }
}

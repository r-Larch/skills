namespace WorkState.Core.Domain;

public enum ItemStatus
{
    Open,
    Ready,
    InProgress,
    Blocked,
    Review,
    Done,
    Cancelled,
    Superseded,
}

public enum ItemResolution
{
    Accepted,
    Reworked,
    Replanned,
    Escalated,
    Skipped,
    Wontfix,
    Duplicate,
    Obsolete,
}

public enum LinkType
{
    Blocks,
    Supersedes,
    Duplicates,
    DerivedFrom,
    Verifies,
}

public enum AssumptionState
{
    Open,
    Confirmed,
    Disproven,
    Obsolete,
}

public enum EvidenceLevel
{
    Asserted,
    Reasoned,
    CodeRead,
    Probed,
    Measured,
}

public enum ReversalCost
{
    Trivial,
    Low,
    Medium,
    High,
    Irreversible,
}

public enum NoteKind
{
    CarryForward,
    DoNotRederive,
    Correction,
    Incident,
    Environment,
    Decision,
    Observation,
}

public enum ConstraintKind
{
    RedLine,
    StackFact,
    Command,
    DomainRule,
    GeneratedCode,
}

public enum ActorRole
{
    Controller,
    Orchestrator,
    Worker,
    Reviewer,
    Human,
    System,
}

public enum VerifyRunner
{
    Worker,
    Orchestrator,
    Ci,
    Human,
}

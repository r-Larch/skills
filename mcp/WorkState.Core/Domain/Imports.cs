namespace WorkState.Core.Domain;

/// <summary>
/// One record <c>work_import</c> wrote, keyed by where it came from: a project and a repo-relative
/// path plus anchor. The key is what makes a second import of the same source an update, and the
/// row is the provenance <c>work_get</c> and the dashboard print. Exactly one target is set.
/// </summary>
public class ImportRecord
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectItemId { get; set; }

    /// <summary>Repo-relative, forward slashes.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>A heading or a line ref; empty when the whole file is the source.</summary>
    public string SourceAnchor { get; set; } = "";

    /// <summary>The record kind as imported: run, phase, task, followup, note, assumption or rule.</summary>
    public string Kind { get; set; } = "";

    public Guid? ItemId { get; set; }
    public Guid? NoteId { get; set; }
    public Guid? AssumptionId { get; set; }
    public Guid? ConstraintId { get; set; }

    /// <summary>The importing agent's one line on why this is still true of the current code.</summary>
    public string Why { get; set; } = "";

    /// <summary>The status an imported item was last left in by the import; anything else means it moved.</summary>
    public string? ImportedStatus { get; set; }

    public Guid? SessionId { get; set; }
    public string ImportedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

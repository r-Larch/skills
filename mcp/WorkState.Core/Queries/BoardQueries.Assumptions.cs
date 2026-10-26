using WorkState.Core.Domain;

namespace WorkState.Core.Queries;

/// <summary>One assumption whole, as <c>work_get</c> prints it — any state, with its import provenance.</summary>
public sealed record AssumptionDetailRow(
    string Ref,
    string QualifiedRef,
    bool Qualified,
    string Statement,
    EvidenceLevel Evidence,
    string? EvidenceNote,
    ReversalCost? Reversal,
    AssumptionState State,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt,
    string? ScopeRef,
    string? RevisitRef,
    ItemStatus? RevisitStatus,
    DateTimeOffset? RevisitAt,
    DateTimeOffset CreatedAt,
    string? SourcePath,
    string? SourceAnchor,
    string? Why,
    string? ImportedBy,
    DateTimeOffset? ImportedAt);

public sealed partial class BoardQueries
{
    /// <summary>
    /// The read twin of <c>work_assume</c>'s lookup: <c>A1</c> anywhere in the workspace,
    /// <c>run-a/A1</c> inside that run only. More than one row back is an ambiguous bare ref.
    /// </summary>
    public async Task<IReadOnlyList<AssumptionDetailRow>> AssumptionByRefAsync(
        Guid workspaceId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var parts = reference.Split('/');
        var qualifier = parts.Length > 1 ? string.Join('/', parts.SkipLast(1)) : null;
        var leaf = parts[^1];

        const string sql =
            """
            SELECT a.ref             AS "Ref",
                   CASE WHEN s.ref IS NULL THEN a.ref ELSE s.ref || '/' || a.ref END
                                     AS "QualifiedRef",
                   s.ref IS NOT NULL AS "Qualified",
                   a.statement       AS "Statement",
                   a.evidence        AS "Evidence",
                   a.evidence_note   AS "EvidenceNote",
                   a.reversal        AS "Reversal",
                   a.state           AS "State",
                   a.resolution_note AS "ResolutionNote",
                   a.resolved_at     AS "ResolvedAt",
                   sc.ref            AS "ScopeRef",
                   r.ref             AS "RevisitRef",
                   r.status          AS "RevisitStatus",
                   a.revisit_at      AS "RevisitAt",
                   a.created_at      AS "CreatedAt",
                   ir.source_path    AS "SourcePath",
                   ir.source_anchor  AS "SourceAnchor",
                   ir.why            AS "Why",
                   ir.imported_by    AS "ImportedBy",
                   ir.updated_at     AS "ImportedAt"
            FROM   assumption a
            LEFT   JOIN item s  ON s.id  = a.ref_scope_id
            LEFT   JOIN item sc ON sc.id = a.scope_item_id
            LEFT   JOIN item r  ON r.id  = a.revisit_item_id
            LEFT   JOIN import_record ir ON ir.assumption_id = a.id
            WHERE  a.workspace_id = @workspace
              AND  lower(a.ref) = lower(@leaf)
              AND  (@qualifier IS NULL OR lower(s.ref) = lower(@qualifier))
            ORDER  BY s.ref;
            """;

        return await ListAsync<AssumptionDetailRow>(
            sql,
            new { workspace = workspaceId, leaf, qualifier },
            cancellationToken);
    }
}

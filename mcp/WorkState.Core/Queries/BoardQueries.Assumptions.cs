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
    /// The qualifier of an assumption that sits on no item: <c>workspace/A1</c>. Such an assumption
    /// has no run or project to be qualified by, so without it a run's own <c>A1</c> left its bare ref
    /// ambiguous and nothing named it alone. <c>WorkWriter.OpenAssumptionsAsync</c> reads it too.
    /// </summary>
    public const string WorkspaceQualifier = "workspace";

    /// <summary>
    /// The read twin of <c>work_assume</c>'s lookup: <c>A1</c> anywhere in the workspace,
    /// <c>run-a/A1</c> inside that run only, <c>workspace/A1</c> on no item. More than one row back
    /// is an ambiguous bare ref.
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
              AND  (@qualifier IS NULL
                    OR lower(s.ref) = lower(@qualifier)
                    OR (a.ref_scope_id IS NULL AND lower(@qualifier) = @everywhere))
            ORDER  BY s.ref;
            """;

        return await ListAsync<AssumptionDetailRow>(
            sql,
            new { workspace = workspaceId, leaf, qualifier, everywhere = WorkspaceQualifier },
            cancellationToken);
    }

    /// <summary>
    /// The form of an assumption ref to print where it will be called back: <paramref name="reference"/>
    /// while it names one assumption, else <c>workspace/A1</c> for one on no item when that names it
    /// alone. A run-qualified ref is returned as it is.
    /// </summary>
    public async Task<string> CallableAssumptionRefAsync(
        Guid workspaceId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (reference.Contains('/', StringComparison.Ordinal)
            || (await AssumptionByRefAsync(workspaceId, reference, cancellationToken)).Count < 2)
        {
            return reference;
        }

        var qualified = $"{WorkspaceQualifier}/{reference}";
        return (await AssumptionByRefAsync(workspaceId, qualified, cancellationToken)).Count == 1 ? qualified : reference;
    }
}

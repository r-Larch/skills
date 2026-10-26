using WorkState.Core.Domain;

namespace WorkState.Core.Queries;

/// <summary>A run under a project, as the project brief lists it.</summary>
public sealed record ProjectRunRow(
    Guid Id,
    Guid WorkspaceId,
    string Ref,
    string Kind,
    string Title,
    string? Intent,
    ItemStatus Status,
    Guid? RefScopeId,
    Guid? ParentId,
    string? QualifiedRef,
    string? Resolution,
    DateTimeOffset? ClosedAt,
    bool Backlog,
    int TasksDone,
    int TasksTotal,
    int Unfinished,
    string? Commit)
{
    public ScopeRef Scope => new(Id, WorkspaceId, Ref, Kind, Title, Intent, Status, RefScopeId, ParentId, QualifiedRef);

    public bool Closed => Status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;
}

/// <summary>Open work waiting under a project's backlog run, or a follow-up anywhere under it.</summary>
public sealed record BacklogItemRow(string Ref, string QualifiedRef, string Kind, string Title, bool RefShared);

public sealed partial class BoardQueries
{
    /// <summary>
    /// Every run under a project, open first in <see cref="DefaultScopeAsync"/>'s busiest order, then
    /// closed newest first. <c>Backlog</c> marks the run <c>target:"backlog"</c> defers into, matched
    /// as <c>WorkWriter.BacklogRunAsync</c> matches it.
    /// </summary>
    public async Task<IReadOnlyList<ProjectRunRow>> ProjectRunsAsync(
        Guid projectId,
        string backlogTitle,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {ScopeColumns},
                    i.resolution::text AS "Resolution",
                    i.closed_at        AS "ClosedAt",
                    (i.parent_id = @project AND (lower(i.ref) = 'backlog' OR i.title = @title)) AS "Backlog",
                    coalesce(t.done, 0)::int  AS "TasksDone",
                    coalesce(t.total, 0)::int AS "TasksTotal",
                    (coalesce(r.open_count,0) + coalesce(r.doing_count,0)
                     + coalesce(r.review_count,0))::int AS "Unfinished",
                    h.value AS "Commit"
             FROM   item i
             LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             LEFT JOIN item_rollup r ON r.id = i.id
             LEFT JOIN LATERAL (
                    SELECT count(*) FILTER (WHERE c.status = 'done') AS done, count(*) AS total
                    FROM   item c
                    WHERE  c.ancestors @> ARRAY[i.id]::uuid[] AND c.kind IN ('task','subtask')) t ON true
             LEFT JOIN LATERAL (
                    SELECT a.value
                    FROM   artifact a
                    JOIN   item c ON c.id = a.item_id
                    WHERE  a.kind = 'commit'
                      AND  (c.id = i.id OR c.ancestors @> ARRAY[i.id]::uuid[])
                    ORDER  BY a.at DESC
                    LIMIT  1) h ON true
             WHERE  i.kind = 'run' AND i.ancestors @> ARRAY[@project]::uuid[]
             ORDER BY (i.status IN ('done','cancelled','superseded')),
                      CASE WHEN i.status IN ('done','cancelled','superseded') THEN 0
                           ELSE coalesce(r.open_count,0) + coalesce(r.doing_count,0)
                                + coalesce(r.review_count,0) END DESC,
                      i.closed_at DESC NULLS LAST,
                      i.seq;
             """;

        return await ListAsync<ProjectRunRow>(sql, new { project = projectId, title = backlogTitle }, cancellationToken);
    }

    /// <summary>
    /// The project's waiting work: open leaves of its backlog run, and open follow-ups anywhere under
    /// the project that sit outside it.
    /// </summary>
    public async Task<IReadOnlyList<BacklogItemRow>> ProjectBacklogAsync(
        Guid projectId,
        Guid? backlogRunId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.ref AS "Ref",
                   CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END AS "QualifiedRef",
                   i.kind  AS "Kind",
                   i.title AS "Title",
                   EXISTS (SELECT 1 FROM item o
                           WHERE  o.workspace_id = i.workspace_id
                             AND  lower(o.ref) = lower(i.ref)
                             AND  o.id <> i.id) AS "RefShared"
            FROM   item i
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            WHERE  i.status NOT IN ('done','cancelled','superseded')
              AND  ((@backlog IS NOT NULL AND i.ancestors @> ARRAY[@backlog]::uuid[]
                     AND i.kind IN ('task','followup','subtask','spike','question','incident'))
                    OR (i.kind = 'followup' AND i.ancestors @> ARRAY[@project]::uuid[]))
            ORDER  BY (@backlog IS NOT NULL AND i.ancestors @> ARRAY[@backlog]::uuid[]) DESC,
                      i.depth, i.seq, i.id;
            """;

        return await ListAsync<BacklogItemRow>(sql, new { project = projectId, backlog = backlogRunId }, cancellationToken);
    }
}

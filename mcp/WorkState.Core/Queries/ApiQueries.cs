using Dapper;
using Npgsql;

namespace WorkState.Core.Queries;

/// <summary>
/// The reads doc 10's JSON contract needs and <see cref="BoardQueries"/> does not have: the same
/// tables, keyed by id and returned whole rather than shaped for one rendered line. Wherever a v1
/// field states something an MCP tool also states — attention, the honesty counts, a callable ref —
/// the API calls the <see cref="BoardQueries"/> method instead, so the two cannot disagree.
/// </summary>
public sealed class ApiQueries(NpgsqlDataSource dataSource)
{
    private const string InScope = "(i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[])";

    private const string LeafKinds = "('task','followup','subtask','spike','question','incident')";

    private const string Closed = "('done','cancelled','superseded')";

    /// <summary>The newest journal seq in the workspace: what a response read after it reflects.</summary>
    public async Task<long> SeqAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await SingleAsync<long>(
            "SELECT coalesce(max(seq), 0) FROM event WHERE workspace_id = @workspace;",
            new { workspace = workspaceId },
            cancellationToken);

    public async Task<IReadOnlyList<RefRow>> RefsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
            SELECT i.id           AS "Id",
                   i.workspace_id AS "WorkspaceId",
                   i.ref          AS "Ref",
                   i.kind         AS "Kind",
                   i.title        AS "Title",
                   i.intent       AS "Intent",
                   i.status       AS "Status",
                   i.ref_scope_id AS "RefScopeId",
                   i.parent_id    AS "ParentId",
                   CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END AS "QualifiedRef",
                   EXISTS (SELECT 1 FROM item o
                           WHERE  o.workspace_id = i.workspace_id
                             AND  lower(o.ref) = lower(i.ref)
                             AND  o.id <> i.id) AS "RefShared",
                   i.opened_at    AS "OpenedAt",
                   {BoardQueries.FullRefColumn} AS "FullRef"
            FROM   item i
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            {BoardQueries.NearestProject}
            WHERE  i.id = ANY(@ids);
            """;

        return ids.Count == 0 ? [] : await ListAsync<RefRow>(sql, new { ids = ids.ToArray() }, cancellationToken);
    }

    /// <summary>The nearest run and project at or above each item — the item itself outranks its ancestors.</summary>
    public async Task<IReadOnlyList<Placement>> PlacementsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var sql =
            $"""
             SELECT i.id AS "ItemId",
                    ({Nearest("run")})     AS "RunId",
                    ({Nearest("project")}) AS "ProjectId"
             FROM   item i
             WHERE  i.id = ANY(@ids);
             """;

        return ids.Count == 0 ? [] : await ListAsync<Placement>(sql, new { ids = ids.ToArray() }, cancellationToken);
    }

    private static string Nearest(string kind)
        => $"""
            SELECT a.id FROM item a
            WHERE  a.kind = '{kind}' AND (a.id = i.id OR a.id = ANY(i.ancestors))
            ORDER  BY (a.id = i.id) DESC, array_position(i.ancestors, a.id) DESC NULLS LAST
            LIMIT  1
            """;

    /// <summary>The path from the root to the parent, in tree order.</summary>
    public async Task<IReadOnlyList<Guid>> AncestorsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT u.id
            FROM   item i
            CROSS  JOIN LATERAL unnest(i.ancestors) WITH ORDINALITY AS u(id, ord)
            WHERE  i.id = @id
            ORDER  BY u.ord;
            """;

        return await ListAsync<Guid>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<LeafRollup>> RollupsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT p.id AS "Id",
                    count(c.id) FILTER (WHERE c.status IN ('open','ready'))::int AS "Open",
                    count(c.id) FILTER (WHERE c.status = 'in_progress')::int    AS "InProgress",
                    count(c.id) FILTER (WHERE c.status = 'blocked')::int        AS "Blocked",
                    count(c.id) FILTER (WHERE c.status = 'review')::int         AS "Review",
                    count(c.id) FILTER (WHERE c.status = 'done')::int           AS "Done",
                    count(c.id) FILTER (WHERE c.status = 'cancelled')::int      AS "Cancelled",
                    count(c.id) FILTER (WHERE c.status = 'superseded')::int     AS "Superseded"
             FROM   item p
             LEFT   JOIN item c ON c.ancestors @> ARRAY[p.id] AND c.kind IN {LeafKinds}
             WHERE  p.id = ANY(@ids)
             GROUP  BY p.id;
             """;

        return ids.Count == 0 ? [] : await ListAsync<LeafRollup>(sql, new { ids = ids.ToArray() }, cancellationToken);
    }

    /// <summary>
    /// One claim per claimed item under the scopes — the predicate of
    /// <see cref="BoardQueries.LiveClaimsAsync"/>, so WORKING NOW keeps a lapsed lease the sweep released.
    /// </summary>
    public async Task<IReadOnlyList<ClaimDetail>> HeldClaimsAsync(
        IReadOnlyCollection<Guid> scopeIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT DISTINCT ON (c.item_id)
                    {ClaimColumns}
             FROM   claim c
             JOIN   item i ON i.id = c.item_id
             WHERE  (c.released_at IS NULL OR c.release_reason = 'expired')
               AND  i.status NOT IN {Closed}
               AND  (i.id = ANY(@scopes) OR i.ancestors && @scopes)
             ORDER  BY c.item_id, (c.released_at IS NULL) DESC, c.expires_at DESC;
             """;

        return scopeIds.Count == 0
            ? []
            : await ListAsync<ClaimDetail>(sql, new { scopes = scopeIds.ToArray() }, cancellationToken);
    }

    /// <summary>What each session holds now: its unreleased claims, a lapsed one included until the sweep.</summary>
    public async Task<IReadOnlyList<ClaimDetail>> SessionClaimsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {ClaimColumns}
             FROM   claim c
             WHERE  c.released_at IS NULL AND c.session_id = ANY(@sessions)
             ORDER  BY c.acquired_at DESC, c.id;
             """;

        return sessionIds.Count == 0
            ? []
            : await ListAsync<ClaimDetail>(sql, new { sessions = sessionIds.ToArray() }, cancellationToken);
    }

    private const string ClaimColumns =
        """
        c.id           AS "Id",
        c.item_id      AS "ItemId",
        c.session_id   AS "SessionId",
        c.agent_name   AS "AgentName",
        c.role         AS "Role",
        c.tree         AS "Tree",
        c.acquired_at  AS "AcquiredAt",
        c.heartbeat_at AS "HeartbeatAt",
        c.expires_at   AS "ExpiresAt",
        c.released_at  AS "ReleasedAt"
        """;

    private const string SessionColumns =
        """
        s.id                AS "Id",
        s.name              AS "Name",
        s.role              AS "Role",
        s.external_role     AS "ExternalRole",
        s.origin            AS "Origin",
        s.launched_by       AS "LaunchedBy",
        s.claude_session_id AS "ClaudeSessionId",
        s.cwd               AS "Cwd",
        s.tree              AS "Tree",
        s.client_name       AS "ClientName",
        s.client_version    AS "ClientVersion",
        s.first_seen_at     AS "FirstSeenAt",
        s.last_seen_at      AS "LastSeenAt"
        """;

    public async Task<IReadOnlyList<SessionRow>> SessionsByIdAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        const string sql = $"SELECT {SessionColumns} FROM agent_session s WHERE s.id = ANY(@ids);";

        return ids.Count == 0 ? [] : await ListAsync<SessionRow>(sql, new { ids = ids.ToArray() }, cancellationToken);
    }

    /// <summary>doc 14 §2 — each session's token: <c>none</c> (header-named), <c>live</c>, <c>revoked</c> or <c>expired</c>.</summary>
    public async Task<IReadOnlyList<SessionTokenRow>> SessionTokensAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT s.id AS "Id",
                   CASE WHEN s.token_hash IS NULL AND s.revoked_at IS NULL THEN 'none'
                        WHEN s.revoked_at IS NOT NULL                     THEN 'revoked'
                        WHEN s.token_expires_at <= now()                  THEN 'expired'
                        ELSE 'live' END AS "Token"
            FROM   agent_session s
            WHERE  s.id = ANY(@ids);
            """;

        return ids.Count == 0 ? [] : await ListAsync<SessionTokenRow>(sql, new { ids = ids.ToArray() }, cancellationToken);
    }

    /// <summary>doc 10 <c>/sessions</c>: most recently seen first; <paramref name="active"/> is the last 30 minutes.</summary>
    public async Task<IReadOnlyList<SessionRow>> SessionsAsync(
        Guid workspaceId,
        bool active,
        string? name,
        string? claudeSessionId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {SessionColumns}
             FROM   agent_session s
             WHERE  s.workspace_id = @workspace
               AND  (NOT @active OR s.last_seen_at >= now() - interval '30 minutes')
               AND  (@name IS NULL OR s.name = @name)
               AND  (@claude IS NULL OR s.claude_session_id = @claude)
             ORDER  BY s.last_seen_at DESC, s.name;
             """;

        return await ListAsync<SessionRow>(
            sql,
            new { workspace = workspaceId, active, name, claude = claudeSessionId },
            cancellationToken);
    }

    /// <summary>The runs under a project, with what the overview prints beside each.</summary>
    public async Task<IReadOnlyList<RunRow>> RunsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT r.id             AS "Id",
                    rc.branch        AS "Branch",
                    rc.worktree_path AS "Worktree",
                    (SELECT count(*)::int
                     FROM   claim c JOIN item i ON i.id = c.item_id
                     WHERE  c.released_at IS NULL AND c.expires_at >= now()
                       AND  (i.id = r.id OR i.ancestors @> ARRAY[r.id]::uuid[])) AS "ActiveClaims",
                    (SELECT max(e.at)
                     FROM   event e JOIN item i ON i.id = e.item_id
                     WHERE  i.id = r.id OR i.ancestors @> ARRAY[r.id]::uuid[])  AS "LastEventAt"
             FROM   item r
             LEFT   JOIN run_config rc ON rc.item_id = r.id
             WHERE  r.kind = 'run' AND r.ancestors @> ARRAY[@project]::uuid[]
             ORDER  BY r.seq, r.ref;
             """;

        return await ListAsync<RunRow>(sql, new { project = projectId }, cancellationToken);
    }

    public async Task<(string? Branch, string? Worktree)> RunConfigAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT branch AS "Branch", worktree_path AS "Worktree"
            FROM   run_config WHERE item_id = @run;
            """;

        var row = await SingleAsync<RunConfigRow>(sql, new { run = runId }, cancellationToken);
        return (row?.Branch, row?.Worktree);
    }

    private sealed record RunConfigRow(string? Branch, string? Worktree);

    /// <summary>
    /// The subtree as one flat list in pre-order: the document path sorts a parent before its
    /// children and siblings by seq, and the id makes a tie stable.
    /// </summary>
    public async Task<IReadOnlyList<BoardRow>> BoardAsync(Guid scopeId, CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id           AS "Id",
                    i.parent_id    AS "ParentId",
                    i.depth        AS "Depth",
                    i.seq          AS "Seq",
                    i.kind         AS "Kind",
                    i.status       AS "Status",
                    i.resolution   AS "Resolution",
                    i.priority     AS "Priority",
                    i.severity     AS "Severity",
                    i.opened_at    AS "OpenedAt",
                    i.started_at   AS "StartedAt",
                    i.closed_at    AS "ClosedAt",
                    EXISTS (SELECT 1 FROM item ch WHERE ch.parent_id = i.id) AS "HasChildren",
                    coalesce(cr.total, 0)::int AS "CriteriaTotal",
                    coalesce(cr.ok, 0)::int    AS "CriteriaChecked",
                    a.value        AS "Commit",
                    v.passed       AS "VerificationPassed",
                    v.run_by       AS "VerificationRunBy",
                    v.at           AS "VerificationAt",
                    k.requires_verify AS "RequiresVerify",
                    EXISTS (SELECT 1 FROM verification ov
                            WHERE ov.item_id = i.id AND ov.passed AND ov.run_by = 'orchestrator') AS "OrchestratorVerified",
                    EXISTS (SELECT 1 FROM event fe
                            WHERE fe.item_id = i.id AND fe.kind = 'forced_transition')            AS "Forced",
                    EXISTS (SELECT 1 FROM note n
                            WHERE n.to_item_id = i.id AND n.consumed_at IS NULL)                  AS "UnconsumedNotes",
                    (r.open_count + r.doing_count + r.blocked_count + r.review_count + r.done_count)::int AS "RollupTotal",
                    r.done_count::int AS "RollupDone"
             FROM   item i
             JOIN   item_kind k ON k.key = i.kind
             {WorkStateQueries.Rollup}
             {WorkStateQueries.DocumentPath}
             LEFT   JOIN LATERAL (
                      SELECT count(*) AS total, count(*) FILTER (WHERE ic.checked) AS ok
                      FROM   item_criterion ic WHERE ic.item_id = i.id) cr ON true
             LEFT   JOIN LATERAL (
                      SELECT aa.value FROM artifact aa
                      WHERE  aa.item_id = i.id AND aa.kind = 'commit'
                      ORDER  BY aa.at DESC LIMIT 1) a ON true
             LEFT   JOIN LATERAL (
                      SELECT vv.passed, vv.run_by, vv.at FROM verification vv
                      WHERE  vv.item_id = i.id
                      ORDER  BY vv.at DESC, vv.id DESC LIMIT 1) v ON true
             WHERE  {InScope}
             ORDER  BY p.path || i.seq, i.id;
             """;

        return await ListAsync<BoardRow>(sql, new { scope = scopeId }, cancellationToken);
    }

    public async Task<ItemDetailRow?> ItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.intent              AS "Intent",
                   i.verify_cmd          AS "VerifyCmd",
                   to_jsonb(i.risk_tags)::text AS "RiskTags",
                   i.priority            AS "Priority",
                   i.severity            AS "Severity",
                   i.source              AS "Source",
                   i.resolution          AS "Resolution",
                   i.closed_by           AS "ClosedBy",
                   i.opened_at           AS "OpenedAt",
                   i.started_at          AS "StartedAt",
                   i.closed_at           AS "ClosedAt"
            FROM   item i WHERE i.id = @id;
            """;

        return await SingleAsync<ItemDetailRow>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<CriterionDetail>> CriteriaAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT seq AS "Seq", text AS "Text", checked AS "Checked", evidence AS "Evidence", checked_at AS "CheckedAt"
            FROM   item_criterion WHERE item_id = @id
            ORDER  BY seq;
            """;

        return await ListAsync<CriterionDetail>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<ReportRow>> ReportsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT a.n          AS "Attempt",
                   a.agent_name AS "AgentName",
                   a.started_at AS "StartedAt",
                   a.ended_at   AS "EndedAt",
                   r.status     AS "Status",
                   r.files::text AS "Files",
                   r.deviations AS "Deviations",
                   r.risks      AS "Risks",
                   r.not_done   AS "NotDone",
                   r.raw        AS "Raw",
                   r.at         AS "At",
                   r.session_id AS "SessionId"
            FROM   report r
            JOIN   attempt a ON a.id = r.attempt_id
            WHERE  a.item_id = @id
            ORDER  BY a.n, r.at, r.id;
            """;

        return await ListAsync<ReportRow>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<VerificationDetail>> VerificationsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT command      AS "Command",
                   result_line  AS "ResultLine",
                   passed       AS "Passed",
                   counts::text AS "Counts",
                   run_by       AS "RunBy",
                   session_id   AS "SessionId",
                   red_check::text AS "RedCheck",
                   at           AS "At"
            FROM   verification WHERE item_id = @id
            ORDER  BY at, id;
            """;

        return await ListAsync<VerificationDetail>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactRow>> ArtifactsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT kind AS "Kind", value AS "Value", at AS "At"
            FROM   artifact WHERE item_id = @id
            ORDER  BY at, id;
            """;

        return await ListAsync<ArtifactRow>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<ChildRow>> ChildrenAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT c.id AS "Id",
                   (SELECT count(*)::int FROM item_criterion ic WHERE ic.item_id = c.id) AS "CriteriaTotal",
                   (SELECT count(*)::int FROM item_criterion ic WHERE ic.item_id = c.id AND ic.checked) AS "CriteriaChecked"
            FROM   item c WHERE c.parent_id = @id
            ORDER  BY c.seq, c.id;
            """;

        return await ListAsync<ChildRow>(sql, new { id = itemId }, cancellationToken);
    }

    /// <summary>A finding is against the item it sits under — the same reading the blocker attention line makes.</summary>
    public async Task<IReadOnlyList<FindingRow>> FindingsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT f.id AS "Id", f.severity AS "Severity", f.source AS "Source"
            FROM   item f WHERE f.parent_id = @id AND f.kind = 'finding'
            ORDER  BY f.seq, f.id;
            """;

        return await ListAsync<FindingRow>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<NoteDetail>> NotesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT id AS "Id", kind AS "Kind", body AS "Body", from_item_id AS "FromItemId",
                   to_item_id AS "ToItemId", consumed_at AS "ConsumedAt", created_at AS "CreatedAt",
                   session_id AS "SessionId"
            FROM   note WHERE to_item_id = @id OR from_item_id = @id
            ORDER  BY created_at, id;
            """;

        return await ListAsync<NoteDetail>(sql, new { id = itemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<AssumptionDetail>> AssumptionsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT ref AS "Ref", statement AS "Statement", state AS "State", revisit_item_id AS "RevisitItemId"
            FROM   assumption WHERE scope_item_id = @id
            ORDER  BY ref;
            """;

        return await ListAsync<AssumptionDetail>(sql, new { id = itemId }, cancellationToken);
    }

    /// <summary>
    /// The journal, under a scope or on one item exactly. <paramref name="kinds"/> null means every
    /// kind. Ordered for paging, as <see cref="BoardQueries.EventsAsync"/> is and for the same reason.
    /// <paramref name="beforeSeq"/> bounds it from above, exclusive.
    /// </summary>
    public async Task<IReadOnlyList<EventDetail>> EventsAsync(
        Guid workspaceId,
        Guid? scopeId,
        bool exact,
        long sinceSeq,
        int limit,
        bool oldestFirst,
        IReadOnlyCollection<string>? kinds,
        CancellationToken cancellationToken = default,
        long? beforeSeq = null)
    {
        var sql =
            $"""
             SELECT e.seq          AS "Seq",
                    e.item_id      AS "ItemId",
                    e.at           AS "At",
                    e.actor        AS "Actor",
                    e.role         AS "Role",
                    e.kind         AS "Kind",
                    e.summary      AS "Summary",
                    e.payload::text AS "Payload",
                    e.session_id   AS "SessionId"
             FROM   event e
             WHERE  e.workspace_id = @workspace
               AND  e.seq > @since
               AND  (@before IS NULL OR e.seq < @before)
               AND  (@scope IS NULL
                     OR e.item_id = @scope
                     OR (NOT @exact AND EXISTS (SELECT 1 FROM item x
                                                WHERE x.id = e.item_id AND x.ancestors @> ARRAY[@scope]::uuid[])))
               AND  (@kinds IS NULL OR e.kind = ANY(string_to_array(@kinds, ',')))
             ORDER  BY e.seq {(oldestFirst ? "ASC" : "DESC")}
             LIMIT  @limit;
             """;

        return await ListAsync<EventDetail>(
            sql,
            new
            {
                workspace = workspaceId,
                scope = scopeId,
                exact,
                since = sinceSeq,
                before = beforeSeq,
                limit,
                kinds = kinds is null ? null : string.Join(",", kinds),
            },
            cancellationToken);
    }

    /// <summary>
    /// <c>blocked</c> items waiting on a person. One still behind an open <c>blocks</c> edge waits on
    /// work instead (doc 10 inbox), and the frontier will hand it out when that work closes.
    /// </summary>
    public async Task<IReadOnlyList<WaitingRow>> BlockedOnAPersonAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id AS "Id",
                    coalesce((SELECT max(e.at) FROM event e
                              WHERE e.item_id = i.id AND e.kind IN ('status','forced_transition')),
                             i.started_at, i.opened_at) AS "Since"
             FROM   item i
             WHERE  i.status = 'blocked'
               AND  {InScope}
               AND  NOT EXISTS (SELECT 1 FROM item_link l JOIN item b ON b.id = l.from_item_id
                                WHERE l.to_item_id = i.id AND l.type = 'blocks'
                                  AND b.status NOT IN {Closed})
             ORDER  BY i.seq, i.id;
             """;

        return await ListAsync<WaitingRow>(sql, new { scope = scopeId }, cancellationToken);
    }

    public async Task<IReadOnlyList<WaitingRow>> OpenQuestionsAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id AS "Id", i.opened_at AS "Since"
             FROM   item i
             WHERE  i.kind = 'question' AND i.status NOT IN {Closed} AND {InScope}
             ORDER  BY i.seq, i.id;
             """;

        return await ListAsync<WaitingRow>(sql, new { scope = scopeId }, cancellationToken);
    }

    /// <summary>
    /// The project-level backlog run: an open run named "backlog" under the project, else the one
    /// this server made — the run <c>target:"backlog"</c> defers into (<c>WorkWriter.BacklogRunAsync</c>).
    /// </summary>
    public async Task<Guid?> BacklogRunAsync(
        Guid projectId,
        string title,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT id FROM item
             WHERE  parent_id = @project
               AND  kind = 'run'
               AND  status NOT IN {Closed}
               AND  (lower(ref) = 'backlog' OR title = @title)
             ORDER  BY (lower(ref) = 'backlog') DESC, seq
             LIMIT  1;
             """;

        return await SingleAsync<Guid?>(sql, new { project = projectId, title }, cancellationToken);
    }

    /// <summary>Open work in the backlog run, each with the item it was derived from, if any.</summary>
    public async Task<IReadOnlyList<BacklogRow>> BacklogAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id AS "Id",
                    (SELECT l.to_item_id FROM item_link l
                     WHERE  l.from_item_id = i.id AND l.type = 'derived_from'
                     ORDER  BY l.created_at, l.id LIMIT 1) AS "OriginId"
             FROM   item i
             {WorkStateQueries.DocumentPath}
             WHERE  i.ancestors @> ARRAY[@scope]::uuid[]
               AND  i.kind IN {LeafKinds}
               AND  i.status NOT IN {Closed}
             ORDER  BY p.path || i.seq, i.id;
             """;

        return await ListAsync<BacklogRow>(sql, new { scope = runId }, cancellationToken);
    }

    /// <summary>
    /// Items in the workspace whose ref starts with <paramref name="prefix"/>, case-insensitively and
    /// literally (<c>%</c>, <c>_</c> and <c>\</c> are escaped) — shortest ref first, then the order
    /// <see cref="BoardQueries.ResolveAsync"/> uses.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RefPrefixAsync(
        Guid workspaceId,
        string prefix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.id
            FROM   item i
            JOIN   item_kind k ON k.key = i.kind
            WHERE  i.workspace_id = @workspace
              AND  lower(i.ref) LIKE lower(@pattern) || '%' ESCAPE '\'
            ORDER  BY length(i.ref), k.rank, i.seq
            LIMIT  @limit;
            """;

        var pattern = prefix.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        return await ListAsync<Guid>(sql, new { workspace = workspaceId, pattern, limit }, cancellationToken);
    }

    private async Task<T?> SingleAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<T>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<T>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}

using System.Text.Json;
using Dapper;
using Npgsql;
using WorkState.Core.Queries;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// The four reads doc 15 moved off the <c>item_rollup</c> view, frozen as they were at fc45f08 and
/// run beside the live methods over the 5k seed: every run and project scope must give identical
/// rows in identical order. FactsAsync has no ORDER BY, so its rows are compared sorted by id.
/// </summary>
public static class RollupEquivalence
{
    private const string DocumentPath =
        """
        CROSS JOIN LATERAL (
          SELECT coalesce(array_agg(a.seq ORDER BY u.ord), '{}'::numeric[]) AS path
          FROM   unnest(i.ancestors) WITH ORDINALITY AS u(id, ord)
          JOIN   item a ON a.id = u.id
        ) p
        """;

    private const string OldSubtree =
        $"""
         SELECT i.id              AS "Id",
                i.parent_id       AS "ParentId",
                i.ref             AS "Ref",
                i.kind            AS "Kind",
                i.title           AS "Title",
                i.status          AS "Status",
                i.depth           AS "Depth",
                i.seq             AS "Seq",
                r.open_count      AS "OpenCount",
                r.doing_count     AS "DoingCount",
                r.blocked_count   AS "BlockedCount",
                r.review_count    AS "ReviewCount",
                r.done_count      AS "DoneCount"
         FROM   item i
         JOIN   item_rollup r ON r.id = i.id
         {DocumentPath}
         WHERE  i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[]
         ORDER BY p.path || i.seq;
         """;

    private const string OldDefaultScope =
        """
        SELECT i.id           AS "Id",
               i.workspace_id AS "WorkspaceId",
               i.ref          AS "Ref",
               i.kind         AS "Kind",
               i.title        AS "Title",
               i.intent       AS "Intent",
               i.status       AS "Status",
               i.ref_scope_id AS "RefScopeId",
               i.parent_id    AS "ParentId",
               CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END AS "QualifiedRef"
        FROM   item i
        LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
        JOIN   item_kind k ON k.key = i.kind
        LEFT JOIN item_rollup r ON r.id = i.id
        WHERE  i.workspace_id = @workspace AND i.kind IN ('run','project')
          AND  (@under::uuid IS NULL OR i.id = @under OR i.ancestors @> ARRAY[@under]::uuid[])
        ORDER BY EXISTS (SELECT 1 FROM item ap
                         WHERE  (ap.id = i.id OR ap.id = ANY(i.ancestors))
                           AND  ap.archived_at IS NOT NULL),
                 (i.kind = 'run') DESC,
                 (coalesce(r.open_count,0) + coalesce(r.doing_count,0)
                  + coalesce(r.review_count,0)) DESC,
                 k.rank, i.seq
        LIMIT 1;
        """;

    private const string OldBoard =
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
         JOIN   item_rollup r ON r.id = i.id
         {DocumentPath}
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
         WHERE  (i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[])
         ORDER  BY p.path || i.seq, i.id;
         """;

    private const string OldFacts =
        """
        SELECT i.id                         AS "Id",
               i.ref                        AS "Ref",
               CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                            AS "QualifiedRef",
               EXISTS (SELECT 1 FROM item o
                       WHERE  o.workspace_id = i.workspace_id
                         AND  lower(o.ref) = lower(i.ref)
                         AND  o.id <> i.id) AS "RefShared",
               coalesce(c.total, 0)::int    AS "CriteriaTotal",
               coalesce(c.ok, 0)::int       AS "CriteriaChecked",
               coalesce(v.passed, 0)::int   AS "VerificationsPassed",
               coalesce(v.orch, 0)::int     AS "OrchestratorVerifications",
               a.value                      AS "Commit",
               coalesce(r.open_count, 0) + coalesce(r.doing_count, 0)
                 + coalesce(r.blocked_count, 0)
                 + coalesce(r.review_count, 0)                         AS "OpenChildren",
               i.review_required            AS "ReviewRequired",
               k.requires_verify            AS "RequiresVerify",
               i.severity                   AS "Severity",
               p.ref                        AS "ParentRef",
               i.started_at                 AS "StartedAt",
               coalesce(i.resolution = 'obsolete', false) AS "Moved",
               h.label                      AS "HeldBy",
               i.verify_cmd                 AS "VerifyCmd"
        FROM   item i
        JOIN   item_kind k ON k.key = i.kind
        LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
        LEFT   JOIN item p ON p.id = i.parent_id
        LEFT   JOIN LATERAL (
                 SELECT count(*) AS total, count(*) FILTER (WHERE ic.checked) AS ok
                 FROM   item_criterion ic WHERE ic.item_id = i.id) c ON true
        LEFT   JOIN LATERAL (
                 SELECT count(*) FILTER (WHERE vv.passed) AS passed,
                        count(*) FILTER (WHERE vv.passed AND vv.run_by = 'orchestrator') AS orch
                 FROM   verification vv WHERE vv.item_id = i.id) v ON true
        LEFT   JOIN LATERAL (
                 SELECT aa.value FROM artifact aa
                 WHERE  aa.item_id = i.id AND aa.kind = 'commit'
                 ORDER  BY aa.at DESC LIMIT 1) a ON true
        LEFT   JOIN LATERAL (
                 SELECT coalesce(hs.name, cc.agent_name) AS label
                 FROM   claim cc LEFT JOIN agent_session hs ON hs.id = cc.session_id
                 WHERE  cc.item_id = i.id AND cc.released_at IS NULL AND cc.expires_at >= now()
                 LIMIT  1) h ON true
        LEFT   JOIN item_rollup r ON r.id = i.id
        WHERE  i.id = ANY(@ids);
        """;

    /// <summary>Returns how many rows were compared, per query.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> AssertSameAsync(NpgsqlDataSource source, Guid workspaceId)
    {
        var queries = new WorkStateQueries(source);
        var board = new BoardQueries(source);
        var api = new ApiQueries(source);
        var compared = new Dictionary<string, int> { ["subtree"] = 0, ["default-scope"] = 0, ["board"] = 0, ["facts"] = 0 };

        await using var connection = await source.OpenConnectionAsync(Ct);
        var scopes = (await connection.QueryAsync<Guid>(
            "SELECT id FROM item WHERE workspace_id = @ws AND kind IN ('run','project') ORDER BY seq, id;",
            new { ws = workspaceId })).ToList();

        Same("default-scope", null, await board.DefaultScopeAsync(workspaceId, Ct),
            await connection.QuerySingleOrDefaultAsync<ScopeRef>(OldDefaultScope, new { workspace = workspaceId, under = (Guid?)null }));
        compared["default-scope"]++;

        foreach (var scope in scopes)
        {
            var subtree = await queries.SubtreeAsync(scope, Ct);
            Same("subtree", scope, subtree, (await connection.QueryAsync<SubtreeNode>(OldSubtree, new { scope })).AsList());
            compared["subtree"] += subtree.Count;

            var rows = await api.BoardAsync(scope, Ct);
            Same("board", scope, rows, (await connection.QueryAsync<BoardRow>(OldBoard, new { scope })).AsList());
            compared["board"] += rows.Count;

            Same("default-scope", scope, await board.DefaultScopeAsync(workspaceId, scope, Ct),
                await connection.QuerySingleOrDefaultAsync<ScopeRef>(OldDefaultScope, new { workspace = workspaceId, under = (Guid?)scope }));
            compared["default-scope"]++;

            var ids = subtree.Select(n => n.Id).ToArray();
            var facts = (await board.FactsAsync(ids, Ct)).OrderBy(f => f.Id).ToList();
            Same("facts", scope, facts,
                (await connection.QueryAsync<ItemFacts>(OldFacts, new { ids })).OrderBy(f => f.Id).ToList());
            compared["facts"] += facts.Count;
        }

        return compared;
    }

    private static void Same<T>(string query, Guid? scope, T live, T frozen)
    {
        var (now, then) = (JsonSerializer.Serialize(live), JsonSerializer.Serialize(frozen));
        Assert.True(now == then, $"{query} under {scope?.ToString() ?? "the workspace"} differs from its item_rollup form");
    }
}

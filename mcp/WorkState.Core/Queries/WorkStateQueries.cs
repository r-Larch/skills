using System.Data;
using Dapper;
using Npgsql;

namespace WorkState.Core.Queries;

/// <summary>
/// The read side, in hand-written SQL rather than LINQ: these are recursive, array-containment and
/// full-text queries that EF translates badly and that sit on the hot path of every tool call
/// (doc 03 §1). Column aliases are quoted so the mapping to each record is exact, not conventional.
/// </summary>
public sealed class WorkStateQueries(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// The document path of <c>i</c> — every ancestor's <c>seq</c> in tree order — exposed as
    /// <c>p.path</c>, so <c>p.path || i.seq</c> sorts a whole subtree the way the plan reads.
    /// <c>seq</c> alone orders siblings, so on its own it leaves the order between phases arbitrary
    /// (doc 02 §4, corrected by import finding #4). Both the frontier and the subtree order by this.
    /// </summary>
    internal const string DocumentPath =
        """
        CROSS JOIN LATERAL (
          SELECT coalesce(array_agg(a.seq ORDER BY u.ord), '{}'::numeric[]) AS path
          FROM   unnest(i.ancestors) WITH ORDINALITY AS u(id, ord)
          JOIN   item a ON a.id = u.id
        ) p
        """;

    /// <summary>
    /// <c>item_rollup</c> for the row <c>i</c>, as <c>r</c>, with the view's buckets (RollupReview
    /// migration). Joined row by row, the view is aggregated for every item in the store before the
    /// join narrows it — 20 ms and a JIT compile at 5,000 items (doc 15); this counts only
    /// <c>i</c>'s own subtree.
    /// </summary>
    internal const string Rollup =
        """
        CROSS JOIN LATERAL (
          SELECT count(*) FILTER (WHERE c.status IN ('open','ready')) AS open_count,
                 count(*) FILTER (WHERE c.status = 'in_progress')     AS doing_count,
                 count(*) FILTER (WHERE c.status = 'blocked')         AS blocked_count,
                 count(*) FILTER (WHERE c.status = 'review')          AS review_count,
                 count(*) FILTER (WHERE c.status = 'done')            AS done_count
          FROM   item c
          WHERE  c.ancestors @> ARRAY[i.id]
        ) r
        """;

    /// <summary>
    /// Remaining work under <paramref name="scopeItemId"/> that nothing unfinished blocks and that
    /// has no open children of its own. doc 02 §4 — the single hottest query.
    /// </summary>
    public async Task<IReadOnlyList<ReadyItem>> ReadyFrontierAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             WITH scope AS (
               SELECT id FROM item WHERE id = @scope OR ancestors @> ARRAY[@scope]::uuid[]
             )
             SELECT i.id         AS "Id",
                    i.ref        AS "Ref",
                    i.kind       AS "Kind",
                    i.title      AS "Title",
                    i.verify_cmd AS "VerifyCmd",
                    i.risk_tags  AS "RiskTags",
                    i.priority   AS "Priority",
                    i.seq        AS "Seq"
             FROM   item i
             JOIN   scope s ON s.id = i.id
             {DocumentPath}
             WHERE  i.status IN ('open','ready')
               AND  NOT EXISTS (
                     SELECT 1 FROM item_link l JOIN item b ON b.id = l.from_item_id
                     WHERE l.to_item_id = i.id AND l.type = 'blocks'
                       AND b.status NOT IN ('done','cancelled','superseded'))
               AND  NOT EXISTS (
                     SELECT 1 FROM item c WHERE c.parent_id = i.id
                       AND c.status NOT IN ('done','cancelled','superseded'))
             ORDER BY i.priority DESC, p.path || i.seq;
             """;

        return await QueryAsync<ReadyItem>(sql, new { scope = scopeItemId }, cancellationToken);
    }

    /// <summary>doc 02 §4. The scope filter is additive; everything else is the document's query.</summary>
    public async Task<IReadOnlyList<UnclosedDependency>> UnclosedDependenciesAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT t.ref            AS "Waiting",
                   b.ref            AS "On",
                   b.status         AS "Status",
                   b.seq < t.seq    AS "OrderedCorrectly"
            FROM   item_link l
            JOIN   item t ON t.id = l.to_item_id
            JOIN   item b ON b.id = l.from_item_id
            WHERE  l.type = 'blocks'
              AND  (t.id = @scope OR t.ancestors @> ARRAY[@scope]::uuid[])
              AND  t.status NOT IN ('done','cancelled','superseded')
              AND  (b.status IN ('cancelled','superseded')
                    OR (b.status = 'open' AND b.parent_id IS NULL)
                    OR b.seq > t.seq)
            ORDER BY t.seq;
            """;

        return await QueryAsync<UnclosedDependency>(sql, new { scope = scopeItemId }, cancellationToken);
    }

    public async Task<ItemRollup?> RollupAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT id            AS "Id",
                   open_count    AS "OpenCount",
                   doing_count   AS "DoingCount",
                   blocked_count AS "BlockedCount",
                   review_count  AS "ReviewCount",
                   done_count    AS "DoneCount"
            FROM   item_rollup WHERE id = @item;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ItemRollup>(
            new CommandDefinition(sql, new { item = itemId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The subtree in document order — siblings by <c>seq</c> at every level, not by depth — with
    /// each node's rollup alongside it.
    /// </summary>
    public async Task<IReadOnlyList<SubtreeNode>> SubtreeAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
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
             {Rollup}
             {DocumentPath}
             WHERE  i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[]
             ORDER BY p.path || i.seq;
             """;

        return await QueryAsync<SubtreeNode>(sql, new { scope = scopeItemId }, cancellationToken);
    }

    /// <summary>doc 02 §13. Pass a scope to narrow to one run's threads.</summary>
    public async Task<IReadOnlyList<OpenThread>> OpenThreadsAsync(
        Guid workspaceId,
        Guid? scopeItemId = null,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT t.t     AS "Type",
                   t.ref   AS "Ref",
                   t.text  AS "Text",
                   t.scope AS "Scope"
            FROM   open_thread t
            WHERE  t.workspace_id = @workspace
              AND  (@scope IS NULL
                    OR t.scope = @scope
                    OR EXISTS (SELECT 1 FROM item i
                               WHERE i.id = t.scope AND i.ancestors @> ARRAY[@scope]::uuid[]))
            ORDER BY t.t, t.ref;
            """;

        return await QueryAsync<OpenThread>(
            sql,
            new { workspace = workspaceId, scope = scopeItemId },
            cancellationToken);
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid workspaceId,
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => SearchAsync(workspaceId, query, limit, null, false, cancellationToken);

    /// <summary>
    /// Full text over items, assumptions, active constraints and notes. Doc blocks are not searched:
    /// no resource serves them. The non-item vectors are weighted the way <c>item.search</c> is
    /// stored — ref at A, the text at B, a rule's route at C — so one <c>ts_rank</c> orders the whole
    /// union. A note matches on <c>note_search_idx</c>'s expression (NoteSearchIndex migration) and is
    /// weighted only to rank; assumptions and rules are small enough to vectorise per query.
    /// <c>ts_headline</c> runs after the limit: inside a union Postgres no longer postpones it, and
    /// computing it for every match cost more than the search.
    /// <para>
    /// With a scope, a row counts only when it lives inside the subtree: an item by its ancestors, an
    /// assumption or constraint by its scope item, a note by the item it is addressed to, else the item
    /// it came from. Workspace-wide rows (no scope item) are left out of a scoped search.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid workspaceId,
        string query,
        int limit,
        Guid? scopeItemId,
        bool itemsOnly,
        CancellationToken cancellationToken = default)
    {
        const string inScope =
            """
            (@scope IS NULL OR {0} = @scope
                    OR EXISTS (SELECT 1 FROM item w
                               WHERE w.id = {0} AND w.ancestors @> ARRAY[@scope]::uuid[]))
            """;

        var sql =
            $"""
             WITH q AS (SELECT websearch_to_tsquery('english', @query) AS tsq)
             SELECT h."Source", h."Id", h."Ref", h."Title",
                    ts_headline('english', h.text, q.tsq, 'MaxFragments=1,MaxWords=18,MinWords=5') AS "Snippet",
                    h."Rank", h."Tag", h."ScopeId", h."ScopeRef"
             FROM (
               SELECT 'item'   AS "Source",
                      i.id     AS "Id",
                      i.ref    AS "Ref",
                      i.title  AS "Title",
                      coalesce(i.intent, i.title) AS text,
                      ts_rank(i.search, q.tsq) AS "Rank",
                      NULL::text AS "Tag",
                      NULL::uuid AS "ScopeId",
                      NULL::text AS "ScopeRef"
               FROM   item i, q
               WHERE  i.workspace_id = @workspace AND i.search @@ q.tsq
                 AND  (@scope IS NULL OR i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[])
               UNION ALL
               SELECT 'assumption',
                      a.id,
                      CASE WHEN s.ref IS NULL THEN a.ref ELSE s.ref || '/' || a.ref END,
                      a.statement,
                      a.statement,
                      ts_rank(v.search, q.tsq),
                      a.state::text,
                      a.scope_item_id,
                      sc.ref
               FROM   assumption a
               CROSS  JOIN q
               CROSS  JOIN LATERAL (SELECT setweight(to_tsvector('simple', a.ref), 'A')
                                           || setweight(to_tsvector('english', a.statement), 'B') AS search) v
               LEFT   JOIN item s  ON s.id  = a.ref_scope_id
               LEFT   JOIN item sc ON sc.id = a.scope_item_id
               WHERE  NOT @itemsOnly AND a.workspace_id = @workspace AND v.search @@ q.tsq
                 AND  {string.Format(inScope, "a.scope_item_id")}
               UNION ALL
               SELECT 'constraint',
                      c.id,
                      c.ref,
                      c.text,
                      c.text || coalesce(' — ' || c.route, ''),
                      ts_rank(v.search, q.tsq),
                      c.kind::text,
                      c.scope_item_id,
                      sc.ref
               FROM   constraint_rule c
               CROSS  JOIN q
               CROSS  JOIN LATERAL (SELECT setweight(to_tsvector('simple', c.ref), 'A')
                                           || setweight(to_tsvector('english', c.text), 'B')
                                           || setweight(to_tsvector('english', coalesce(c.route, '')), 'C') AS search) v
               LEFT   JOIN item sc ON sc.id = c.scope_item_id
               WHERE  NOT @itemsOnly AND c.workspace_id = @workspace AND c.active AND v.search @@ q.tsq
                 AND  {string.Format(inScope, "c.scope_item_id")}
               UNION ALL
               SELECT 'note',
                      n.id,
                      n.kind::text,
                      n.body,
                      n.body,
                      ts_rank(setweight(to_tsvector('english', n.body), 'B'), q.tsq),
                      n.kind::text,
                      coalesce(n.to_item_id, n.from_item_id),
                      sc.ref
               FROM   note n
               CROSS  JOIN q
               LEFT   JOIN item sc ON sc.id = coalesce(n.to_item_id, n.from_item_id)
               WHERE  NOT @itemsOnly AND n.workspace_id = @workspace AND to_tsvector('english', n.body) @@ q.tsq
                 AND  {string.Format(inScope, "coalesce(n.to_item_id, n.from_item_id)")}
               ORDER BY "Rank" DESC, "Ref"
               LIMIT @limit
             ) h, q
             ORDER BY h."Rank" DESC, h."Ref";
             """;

        return await QueryAsync<SearchHit>(
            sql,
            new { workspace = workspaceId, query, limit, scope = scopeItemId, itemsOnly },
            cancellationToken);
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<T>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    /// <summary>
    /// Npgsql reports a <c>text[]</c> column as <see cref="Array"/>, which Dapper will not match
    /// against a <c>string[]</c> constructor parameter without a handler for the type.
    /// </summary>
    internal sealed class TextArrayHandler : SqlMapper.TypeHandler<string[]>
    {
        public override string[] Parse(object value) => (string[])value;

        public override void SetValue(IDbDataParameter parameter, string[]? value)
            => parameter.Value = value ?? (object)Array.Empty<string>();
    }
}

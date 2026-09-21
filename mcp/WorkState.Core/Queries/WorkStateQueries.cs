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
    private const string DocumentPath =
        """
        CROSS JOIN LATERAL (
          SELECT coalesce(array_agg(a.seq ORDER BY u.ord), '{}'::numeric[]) AS path
          FROM   unnest(i.ancestors) WITH ORDINALITY AS u(id, ord)
          JOIN   item a ON a.id = u.id
        ) p
        """;

    static WorkStateQueries() => SqlMapper.AddTypeHandler(new TextArrayHandler());

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
             JOIN   item_rollup r ON r.id = i.id
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

    /// <summary>Full text over items and doc blocks, ranked together.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid workspaceId,
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            WITH q AS (SELECT websearch_to_tsquery('english', @query) AS tsq)
            SELECT 'item'   AS "Source",
                   i.id     AS "Id",
                   i.ref    AS "Ref",
                   i.title  AS "Title",
                   ts_headline('english', coalesce(i.intent, i.title), q.tsq,
                               'MaxFragments=1,MaxWords=18,MinWords=5') AS "Snippet",
                   ts_rank(i.search, q.tsq) AS "Rank"
            FROM   item i, q
            WHERE  i.workspace_id = @workspace AND i.search @@ q.tsq
            UNION ALL
            SELECT 'doc_block',
                   b.id,
                   d.slug || '#' || b.short_id,
                   coalesce(b.heading, d.title),
                   ts_headline('english', b.body, q.tsq, 'MaxFragments=1,MaxWords=18,MinWords=5'),
                   ts_rank(b.search, q.tsq)
            FROM   doc_block b JOIN doc d ON d.id = b.doc_id, q
            WHERE  d.workspace_id = @workspace AND b.search @@ q.tsq
            ORDER BY "Rank" DESC, "Ref"
            LIMIT @limit;
            """;

        return await QueryAsync<SearchHit>(
            sql,
            new { workspace = workspaceId, query, limit },
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
    private sealed class TextArrayHandler : SqlMapper.TypeHandler<string[]>
    {
        public override string[] Parse(object value) => (string[])value;

        public override void SetValue(IDbDataParameter parameter, string[]? value)
            => parameter.Value = value ?? (object)Array.Empty<string>();
    }
}

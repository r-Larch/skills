using WorkState.Core.Data;
using WorkState.Core.Domain;

namespace WorkState.Core.Queries;

public sealed record ProjectRepoRow(Guid ProjectId, string Kind, string Key);

public sealed record SessionRepo(string? Remote, string? Root)
{
    public IReadOnlyList<RepoKey> Keys() => RepoKeys.Of(Remote, Root);
}

public sealed partial class BoardQueries
{
    /// <summary>A binding <c>@user</c> may use: any remote, and a root of theirs — of no user's when they have none.</summary>
    private const string Mine = "(pr.kind = 'remote' OR pr.user_id IS NOT DISTINCT FROM @user)";

    private static Guid? ScopeUser() => TenantScope.TryGet(out _, out var user) ? user : null;

    /// <summary>
    /// The project a repository is bound to. Either key matching is enough; when the two keys name
    /// different projects the remote wins, because a root moves with a re-clone and a remote does not.
    /// A root matches only the caller's own binding — a path is a folder on one person's machine — and a remote anyone's.
    /// </summary>
    public async Task<ScopeRef?> ProjectByRepoAsync(
        Guid workspaceId,
        IReadOnlyList<RepoKey> keys,
        CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
        {
            return null;
        }

        const string sql =
            $"""
             SELECT {ScopeColumns}
             FROM   project_repo pr
             JOIN   item i ON i.id = pr.project_item_id
             LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             WHERE  pr.workspace_id = @workspace AND pr.key = ANY(@keys) AND {Mine}
             ORDER  BY (pr.kind = 'remote') DESC, pr.added_at
             LIMIT  1;
             """;

        return await SingleAsync<ScopeRef>(
            sql,
            new { workspace = workspaceId, keys = keys.Select(k => k.Key).ToArray(), user = ScopeUser() },
            cancellationToken);
    }

    /// <summary>The projects' remotes, and the roots bound on the caller's machine.</summary>
    public async Task<IReadOnlyList<ProjectRepoRow>> ProjectReposAsync(
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT pr.project_item_id AS "ProjectId", pr.kind AS "Kind", pr.key AS "Key"
             FROM   project_repo pr
             WHERE  pr.project_item_id = ANY(@ids) AND {Mine}
             ORDER  BY (pr.kind = 'remote') DESC, pr.added_at, pr.key;
             """;

        return await ListAsync<ProjectRepoRow>(sql, new { ids = projectIds.ToArray(), user = ScopeUser() }, cancellationToken);
    }

    /// <summary>The repository a minted session was launched in, stored at mint.</summary>
    public async Task<SessionRepo?> SessionRepoAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT repo_remote AS "Remote", repo_root AS "Root"
            FROM   agent_session
            WHERE  id = @id AND (repo_remote IS NOT NULL OR repo_root IS NOT NULL);
            """;

        return await SingleAsync<SessionRepo>(sql, new { id = sessionId }, cancellationToken);
    }

    /// <summary>Project refs for an onboarding that offers binding to one that exists, by ref.</summary>
    public async Task<IReadOnlyList<string>> ProjectRefsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT ref FROM item
            WHERE  workspace_id = @workspace AND kind = 'project'
              AND  status NOT IN ('cancelled','superseded')
              AND  archived_at IS NULL
            ORDER  BY ref;
            """;

        return await ListAsync<string>(sql, new { workspace = workspaceId }, cancellationToken);
    }

    /// <summary>When the project the item sits in (or is) was archived; null while it is not.</summary>
    public async Task<DateTimeOffset?> ArchivedAtAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT p.archived_at
            FROM   item i
            JOIN   item p ON (p.id = i.id OR p.id = ANY(i.ancestors)) AND p.kind = 'project'
            WHERE  i.id = @id AND p.archived_at IS NOT NULL
            LIMIT  1;
            """;

        return await SingleAsync<DateTimeOffset?>(sql, new { id = itemId }, cancellationToken);
    }
}

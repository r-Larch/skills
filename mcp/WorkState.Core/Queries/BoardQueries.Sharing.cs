namespace WorkState.Core.Queries;

/// <summary>A project's members, by name, and whether the caller is one of them; no row for a project with none.</summary>
public sealed record ProjectSharingRow(Guid ProjectId, int Members, bool You, string[] Names);

public sealed partial class BoardQueries
{
    /// <summary>
    /// Who shares each project, in one statement under the caller's tenant transaction: project_member's policy
    /// (doc 02 §15b) keeps a project the caller cannot see out of the answer even when its id is asked for.
    /// </summary>
    public async Task<IReadOnlyList<ProjectSharingRow>> ProjectSharingAsync(
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT pm.project_item_id AS "ProjectId",
                   count(*)::int AS "Members",
                   coalesce(bool_or(pm.user_id = @user), false) AS "You",
                   array_agg(u.name ORDER BY lower(u.name), u.id) AS "Names"
            FROM   project_member pm
            JOIN   app_user u ON u.id = pm.user_id
            WHERE  pm.project_item_id = ANY(@ids)
            GROUP  BY pm.project_item_id;
            """;

        return await ListAsync<ProjectSharingRow>(sql, new { ids = projectIds.ToArray(), user = ScopeUser() }, cancellationToken);
    }
}

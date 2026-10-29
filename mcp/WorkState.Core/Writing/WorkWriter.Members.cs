using Dapper;
using Npgsql;

namespace WorkState.Core.Writing;

public sealed record ProjectMemberRow(Guid UserId, string Name, DateTimeOffset AddedAt, string? AddedBy, DateTimeOffset? DisabledAt);

/// <summary>A member of the project's workspace who is not a member of the project.</summary>
public sealed record ProjectCandidateRow(Guid UserId, string Name, DateTimeOffset? DisabledAt);

/// <summary>A project's members and who else in its workspace could be added; none left means tenant-wide.</summary>
public sealed record ProjectMembership(
    Guid ProjectId,
    string Ref,
    IReadOnlyList<ProjectMemberRow> Members,
    IReadOnlyList<ProjectCandidateRow> Candidates);

public enum MembershipChange
{
    Changed,
    Unchanged,

    /// <summary>No project the caller may share has that id — unknown, another tenant's, or one they are not in.</summary>
    NotFound,

    /// <summary>The user to add is not a member of the project's workspace.</summary>
    NotInTenant,

    /// <summary>The user to remove is not a member of the project.</summary>
    NotMember,

    /// <summary>Refused: it would leave the project with no member.</summary>
    LastMember,
}

public sealed record MembershipOutcome(MembershipChange Change, ProjectMembership? Membership);

public sealed partial class WorkWriter
{
    /// <summary>
    /// doc 02 §12b — a project is private to its creator until shared. A credential without a user (a static
    /// token), or a user who is not a member of the workspace (a system administrator visiting it), gives the
    /// project every member of the workspace instead, the rule the ProjectMembers migration applied to every
    /// project that existed before members did.
    /// </summary>
    private static async Task FirstMembersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string creator =
            """
            INSERT INTO project_member (project_item_id, user_id, workspace_id, added_by)
            SELECT @project, m.user_id, m.workspace_id, m.user_id
            FROM   tenant_membership m
            WHERE  m.workspace_id = @workspace AND m.user_id = @user;
            """;

        const string everyone =
            """
            INSERT INTO project_member (project_item_id, user_id, workspace_id)
            SELECT @project, m.user_id, m.workspace_id
            FROM   tenant_membership m
            WHERE  m.workspace_id = @workspace;
            """;

        var args = new { project = projectId, workspace = workspaceId, user = userId };
        if (userId is not null
            && await connection.ExecuteAsync(new CommandDefinition(creator, args, transaction, cancellationToken: cancellationToken)) > 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(everyone, args, transaction, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The project's members and the workspace's other users; null unless <paramref name="caller"/> is one of its
    /// members or <paramref name="administers"/> its workspace — so a private project is as unknown as a typo.
    /// </summary>
    public async Task<ProjectMembership?> ProjectMembersAsync(
        Guid workspaceId,
        Guid projectId,
        Guid caller,
        bool administers,
        CancellationToken cancellationToken = default)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var project = await VisibleProjectAsync(connection, transaction, workspaceId, projectId, caller, administers, cancellationToken);
        return project is null
            ? null
            : await MembershipAsync(connection, transaction, workspaceId, project.Value, cancellationToken);
    }

    /// <summary>
    /// Adds <paramref name="userId"/>, or with <paramref name="everyone"/> every member of the workspace who is not
    /// in the project yet — how a project becomes tenant-wide. Only a member of the workspace can be added.
    /// </summary>
    public async Task<MembershipOutcome> AddProjectMemberAsync(
        Guid workspaceId,
        Actor actor,
        Guid projectId,
        Guid caller,
        bool administers,
        Guid? userId,
        bool everyone,
        CancellationToken cancellationToken = default)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var project = await VisibleProjectAsync(connection, transaction, workspaceId, projectId, caller, administers, cancellationToken);
        if (project is null)
        {
            return new MembershipOutcome(MembershipChange.NotFound, null);
        }

        var (id, reference) = project.Value;
        if (!everyone)
        {
            var inTenant = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM tenant_membership WHERE workspace_id = @workspace AND user_id = @user);",
                new { workspace = workspaceId, user = userId },
                transaction,
                cancellationToken: cancellationToken));

            if (!inTenant)
            {
                return new MembershipOutcome(MembershipChange.NotInTenant, null);
            }
        }

        var added = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            WITH added AS (
              INSERT INTO project_member (project_item_id, user_id, workspace_id, added_by)
              SELECT @project, m.user_id, m.workspace_id, @caller
              FROM   tenant_membership m
              WHERE  m.workspace_id = @workspace AND (@everyone OR m.user_id = @user)
              ON CONFLICT (project_item_id, user_id) DO NOTHING
              RETURNING user_id)
            SELECT u.name FROM added a JOIN app_user u ON u.id = a.user_id ORDER BY lower(u.name);
            """,
            new { project = id, workspace = workspaceId, caller, everyone, user = userId },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        if (added.Count > 0)
        {
            await EventAsync(
                connection, transaction, workspaceId, id, actor, "member_added",
                $"{reference}: {string.Join(", ", added)} added as {(added.Count == 1 ? "a member" : "members")} by {actor.Label}",
                new { via = "dashboard", users = added, everyone, by = actor.Label },
                cancellationToken);
        }

        var membership = await MembershipAsync(connection, transaction, workspaceId, project.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipOutcome(added.Count > 0 ? MembershipChange.Changed : MembershipChange.Unchanged, membership);
    }

    /// <summary>
    /// Ends one membership, refusing the project's last. Under the workspace row's lock, like a tenant removal
    /// (<c>AdminStore</c>), so the two cannot each take a different one of a project's last two members.
    /// </summary>
    public async Task<MembershipOutcome> RemoveProjectMemberAsync(
        Guid workspaceId,
        Actor actor,
        Guid projectId,
        Guid caller,
        bool administers,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM workspace WHERE id = @workspace FOR NO KEY UPDATE;",
            new { workspace = workspaceId },
            transaction,
            cancellationToken: cancellationToken));

        var project = await VisibleProjectAsync(connection, transaction, workspaceId, projectId, caller, administers, cancellationToken);
        if (project is null)
        {
            return new MembershipOutcome(MembershipChange.NotFound, null);
        }

        var (id, reference) = project.Value;
        var members = (await connection.QueryAsync<(Guid UserId, string Name)>(new CommandDefinition(
            """
            SELECT pm.user_id, u.name
            FROM   project_member pm JOIN app_user u ON u.id = pm.user_id
            WHERE  pm.project_item_id = @project;
            """,
            new { project = id },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        var removed = members.FirstOrDefault(m => m.UserId == userId);
        if (removed.UserId == Guid.Empty)
        {
            return new MembershipOutcome(MembershipChange.NotMember, null);
        }

        if (members.Count == 1)
        {
            return new MembershipOutcome(MembershipChange.LastMember, null);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_member WHERE project_item_id = @project AND user_id = @user;",
            new { project = id, user = userId },
            transaction,
            cancellationToken: cancellationToken));

        await EventAsync(
            connection, transaction, workspaceId, id, actor, "member_removed",
            $"{reference}: {removed.Name} removed as a member by {actor.Label}",
            new { via = "dashboard", user = removed.Name, by = actor.Label },
            cancellationToken);

        var membership = await MembershipAsync(connection, transaction, workspaceId, project.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipOutcome(MembershipChange.Changed, membership);
    }

    private static async Task<(Guid Id, string Ref)?> VisibleProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        Guid caller,
        bool administers,
        CancellationToken cancellationToken)
    {
        var found = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Ref)>(new CommandDefinition(
            """
            SELECT i.id, i.ref
            FROM   item i
            WHERE  i.id = @project AND i.workspace_id = @workspace AND i.kind = 'project'
              AND  (@administers OR EXISTS (SELECT 1 FROM project_member pm
                                           WHERE pm.project_item_id = i.id AND pm.user_id = @caller));
            """,
            new { project = projectId, workspace = workspaceId, caller, administers },
            transaction,
            cancellationToken: cancellationToken));

        return found.Id == Guid.Empty ? null : found;
    }

    private static async Task<ProjectMembership> MembershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        (Guid Id, string Ref) project,
        CancellationToken cancellationToken)
    {
        var members = await connection.QueryAsync<ProjectMemberRow>(new CommandDefinition(
            """
            SELECT u.id AS "UserId", u.name AS "Name", pm.added_at AS "AddedAt", b.name AS "AddedBy",
                   u.disabled_at AS "DisabledAt"
            FROM   project_member pm
            JOIN   app_user u ON u.id = pm.user_id
            LEFT   JOIN app_user b ON b.id = pm.added_by
            WHERE  pm.project_item_id = @project
            ORDER  BY lower(u.name);
            """,
            new { project = project.Id },
            transaction,
            cancellationToken: cancellationToken));

        var candidates = await connection.QueryAsync<ProjectCandidateRow>(new CommandDefinition(
            """
            SELECT u.id AS "UserId", u.name AS "Name", u.disabled_at AS "DisabledAt"
            FROM   tenant_membership m
            JOIN   app_user u ON u.id = m.user_id
            WHERE  m.workspace_id = @workspace
              AND  NOT EXISTS (SELECT 1 FROM project_member pm WHERE pm.project_item_id = @project AND pm.user_id = m.user_id)
            ORDER  BY lower(u.name);
            """,
            new { project = project.Id, workspace = workspaceId },
            transaction,
            cancellationToken: cancellationToken));

        return new ProjectMembership(project.Id, project.Ref, members.AsList(), candidates.AsList());
    }
}

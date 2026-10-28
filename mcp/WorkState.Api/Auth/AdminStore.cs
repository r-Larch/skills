using Dapper;
using Npgsql;
using WorkState.Core.Domain;

namespace WorkState.Api.Auth;

/// <summary>
/// What a signed-in user may administer, read from the table on every call and never from the cookie: system
/// administrators everything, a tenant admin the workspaces they are an admin of, a member nothing.
/// </summary>
public sealed record AdminRights(Guid UserId, string Name, bool IsSystemAdmin, IReadOnlySet<Guid> Administers)
{
    public bool Any => IsSystemAdmin || Administers.Count > 0;

    public bool Manages(Guid tenant) => IsSystemAdmin || Administers.Contains(tenant);
}

public sealed record AdminTenant(Guid Id, string Slug, DateTimeOffset CreatedAt, int Members, int Admins, int PendingInvites);

public sealed record AdminMember(
    Guid UserId,
    string Name,
    string Role,
    bool IsSystemAdmin,
    DateTimeOffset JoinedAt,
    DateTimeOffset? DisabledAt);

public sealed record AdminUser(
    Guid Id,
    string Name,
    bool IsSystemAdmin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    IReadOnlyList<UserTenant> Tenants);

/// <summary>A role change or a removal, as it came out.</summary>
public enum MemberChange
{
    Changed,
    Unchanged,
    NotMember,

    /// <summary>Refused: it would leave the workspace with no admin.</summary>
    LastAdmin,
}

/// <summary>
/// The admin pages' reads and writes over <c>workspace</c>, <c>tenant_membership</c> and <c>app_user</c>. It
/// judges nothing about who is asking: the endpoints hold an <see cref="AdminRights"/> and ask it first.
/// </summary>
public sealed class AdminStore(NpgsqlDataSource dataSource)
{
    private sealed record RightsRow(string Name, bool IsSystemAdmin);

    private sealed record MembershipRow(Guid UserId, Guid Id, string Slug, string Role);

    private sealed record UserRow(Guid Id, string Name, bool IsSystemAdmin, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt);

    /// <summary>The rights of <paramref name="user"/> as the table has them now; null once they are gone or disabled.</summary>
    public async Task<AdminRights?> RightsAsync(Guid user, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var found = await connection.QuerySingleOrDefaultAsync<RightsRow>(new CommandDefinition(
            """
            SELECT name AS "Name", is_system_admin AS "IsSystemAdmin"
            FROM app_user
            WHERE id = @user AND disabled_at IS NULL;
            """,
            new { user },
            cancellationToken: cancellationToken));

        if (found is null)
        {
            return null;
        }

        var administers = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT workspace_id FROM tenant_membership WHERE user_id = @user AND role = @admin;",
            new { user, admin = TenantMembership.Admin },
            cancellationToken: cancellationToken));

        return new AdminRights(user, found.Name, found.IsSystemAdmin, administers.ToHashSet());
    }

    /// <summary>Every workspace for a system administrator, otherwise the ones they are an admin of; oldest first.</summary>
    public async Task<IReadOnlyList<AdminTenant>> TenantsAsync(AdminRights rights, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<AdminTenant>(new CommandDefinition(
            """
            SELECT w.id AS "Id", w.slug AS "Slug", w.created_at AS "CreatedAt",
                   (SELECT count(*)::int FROM tenant_membership m WHERE m.workspace_id = w.id) AS "Members",
                   (SELECT count(*)::int FROM tenant_membership m WHERE m.workspace_id = w.id AND m.role = @admin) AS "Admins",
                   (SELECT count(*)::int FROM user_link l
                     WHERE l.workspace_id = w.id AND l.kind = @invite AND l.used_at IS NULL AND l.expires_at > now()) AS "PendingInvites"
            FROM workspace w
            WHERE @systemAdmin OR w.id = ANY(@administers)
            ORDER BY w.created_at, w.slug;
            """,
            new
            {
                admin = TenantMembership.Admin,
                invite = UserLink.Invite,
                systemAdmin = rights.IsSystemAdmin,
                administers = rights.Administers.ToArray(),
            },
            cancellationToken: cancellationToken))).AsList();
    }

    /// <summary>The workspace's slug; null when there is no such workspace.</summary>
    public async Task<string?> SlugAsync(Guid tenant, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT slug FROM workspace WHERE id = @tenant;",
            new { tenant },
            cancellationToken: cancellationToken));
    }

    /// <summary>The new workspace, or null when the slug is taken. The slug is already judged (<see cref="AccountRules"/>).</summary>
    public async Task<AdminTenant?> CreateTenantAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AdminTenant>(new CommandDefinition(
            """
            INSERT INTO workspace (id, slug) VALUES (@id, @slug)
            ON CONFLICT (slug) DO NOTHING
            RETURNING id AS "Id", slug AS "Slug", created_at AS "CreatedAt",
                      0 AS "Members", 0 AS "Admins", 0 AS "PendingInvites";
            """,
            new { id = Guid.CreateVersion7(), slug },
            cancellationToken: cancellationToken));
    }

    /// <summary>The workspace's members, admins first, then by name.</summary>
    public async Task<IReadOnlyList<AdminMember>> MembersAsync(Guid tenant, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<AdminMember>(new CommandDefinition(
            """
            SELECT u.id AS "UserId", u.name AS "Name", m.role AS "Role", u.is_system_admin AS "IsSystemAdmin",
                   m.created_at AS "JoinedAt", u.disabled_at AS "DisabledAt"
            FROM tenant_membership m
            JOIN app_user u ON u.id = m.user_id
            WHERE m.workspace_id = @tenant
            ORDER BY m.role = @admin DESC, lower(u.name);
            """,
            new { tenant, admin = TenantMembership.Admin },
            cancellationToken: cancellationToken))).AsList();
    }

    /// <summary>Sets a member's role, refusing to demote the workspace's last admin.</summary>
    public Task<MemberChange> SetRoleAsync(Guid tenant, Guid user, string role, CancellationToken cancellationToken)
        => ChangeAsync(tenant, user, role, cancellationToken);

    /// <summary>Ends a membership, refusing to remove the workspace's last admin.</summary>
    public Task<MemberChange> RemoveAsync(Guid tenant, Guid user, CancellationToken cancellationToken)
        => ChangeAsync(tenant, user, null, cancellationToken);

    /// <summary>
    /// One membership change under the workspace row's lock, so two admins demoting each other at once cannot
    /// both see the other still there. <c>NO KEY UPDATE</c> conflicts with itself and not with the key-share lock
    /// every write referencing the workspace takes.
    /// </summary>
    private async Task<MemberChange> ChangeAsync(Guid tenant, Guid user, string? role, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM workspace WHERE id = @tenant FOR NO KEY UPDATE;",
            new { tenant },
            transaction,
            cancellationToken: cancellationToken));

        var current = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT role FROM tenant_membership WHERE workspace_id = @tenant AND user_id = @user;",
            new { tenant, user },
            transaction,
            cancellationToken: cancellationToken));

        if (current is null)
        {
            return MemberChange.NotMember;
        }

        if (current == role)
        {
            return MemberChange.Unchanged;
        }

        if (current == TenantMembership.Admin)
        {
            var otherAdmins = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM tenant_membership WHERE workspace_id = @tenant AND role = @admin AND user_id <> @user;",
                new { tenant, user, admin = TenantMembership.Admin },
                transaction,
                cancellationToken: cancellationToken));

            if (otherAdmins == 0)
            {
                return MemberChange.LastAdmin;
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            role is null
                ? "DELETE FROM tenant_membership WHERE workspace_id = @tenant AND user_id = @user;"
                : "UPDATE tenant_membership SET role = @role WHERE workspace_id = @tenant AND user_id = @user;",
            new { tenant, user, role },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return MemberChange.Changed;
    }

    /// <summary>Every user with their memberships, by name.</summary>
    public async Task<IReadOnlyList<AdminUser>> UsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var users = await connection.QueryAsync<UserRow>(new CommandDefinition(
            """
            SELECT id AS "Id", name AS "Name", is_system_admin AS "IsSystemAdmin", created_at AS "CreatedAt",
                   disabled_at AS "DisabledAt"
            FROM app_user
            ORDER BY lower(name);
            """,
            cancellationToken: cancellationToken));

        var memberships = (await connection.QueryAsync<MembershipRow>(new CommandDefinition(
            """
            SELECT m.user_id AS "UserId", w.id AS "Id", w.slug AS "Slug", m.role AS "Role"
            FROM tenant_membership m
            JOIN workspace w ON w.id = m.workspace_id
            ORDER BY m.created_at, w.slug;
            """,
            cancellationToken: cancellationToken))).ToLookup(m => m.UserId);

        return
        [
            .. users.Select(u => new AdminUser(
                u.Id,
                u.Name,
                u.IsSystemAdmin,
                u.CreatedAt,
                u.DisabledAt,
                [.. memberships[u.Id].Select(m => new UserTenant(m.Id, m.Slug, m.Role))])),
        ];
    }

    /// <summary>
    /// Disables or re-enables a user; false for an unknown id. Disabling ends their cookies, their OAuth tokens and
    /// the sessions they minted at the next request each makes, because every one of those re-reads the row.
    /// </summary>
    public async Task<(bool Found, DateTimeOffset? DisabledAt)> SetDisabledAsync(
        Guid user,
        bool disabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var found = (await connection.QueryAsync<DateTimeOffset?>(new CommandDefinition(
            """
            UPDATE app_user
               SET disabled_at = CASE WHEN @disabled THEN coalesce(disabled_at, now()) END
             WHERE id = @user
            RETURNING disabled_at;
            """,
            new { user, disabled },
            cancellationToken: cancellationToken))).AsList();

        return found is [var at] ? (true, at) : (false, null);
    }
}

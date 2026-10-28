using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using WorkState.Core.Domain;

namespace WorkState.Api.Auth;

/// <summary>The only place a link's token exists in the clear; the endpoint returns it once and drops it.</summary>
public sealed record CreatedLink(Guid Id, string Token, DateTimeOffset ExpiresAt);

/// <summary>An invite not yet used, as the admin page lists it: never its token, which is not kept.</summary>
public sealed record PendingInvite(Guid Id, string Role, string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>What an invite link joins, for the page that opens it.</summary>
public sealed record InviteInfo(Guid WorkspaceId, string Slug, string Role, DateTimeOffset ExpiresAt);

public enum InviteOutcomeKind
{
    Accepted,

    /// <summary>No usable invite: never one, used, revoked or expired, which the caller is not told apart.</summary>
    Invalid,

    /// <summary>The new user's name belongs to someone already; the invite is still unused.</summary>
    NameTaken,
}

public sealed record InviteOutcome(InviteOutcomeKind Kind, DashboardUser? User = null, Guid Tenant = default);

/// <summary>
/// Invite and reset links (<c>user_link</c>). A link is used by one conditional <c>UPDATE … WHERE used_at IS
/// NULL AND expires_at &gt; now() RETURNING</c>, so of two requests racing with one token exactly one gets a row.
/// </summary>
public sealed class UserLinkStore(NpgsqlDataSource dataSource)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

    private static readonly PasswordHasher<AppUser> Hasher = new();

    private static readonly AppUser Nobody = new();

    private sealed record UsedInvite(Guid WorkspaceId, string Role);

    /// <summary>A new invite into <paramref name="tenant"/> with <paramref name="role"/>, valid for <see cref="InviteLifetime"/>.</summary>
    public async Task<CreatedLink> InviteAsync(Guid tenant, string role, Guid createdBy, CancellationToken cancellationToken)
    {
        var token = LinkToken.New();
        var id = Guid.CreateVersion7();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var expiresAt = await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
            """
            INSERT INTO user_link (id, kind, token_hash, workspace_id, role, created_by, expires_at)
            VALUES (@id, @kind, @hash, @tenant, @role, @createdBy, now() + @lifetime)
            RETURNING expires_at;
            """,
            new { id, kind = UserLink.Invite, hash = LinkToken.Hash(token), tenant, role, createdBy, lifetime = InviteLifetime },
            cancellationToken: cancellationToken));

        return new CreatedLink(id, token, expiresAt);
    }

    /// <summary>The invites into <paramref name="tenant"/> nobody has used yet and that have not expired, newest first.</summary>
    public async Task<IReadOnlyList<PendingInvite>> PendingInvitesAsync(Guid tenant, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<PendingInvite>(new CommandDefinition(
            """
            SELECT l.id AS "Id", l.role AS "Role", u.name AS "CreatedBy", l.created_at AS "CreatedAt",
                   l.expires_at AS "ExpiresAt"
            FROM user_link l
            LEFT JOIN app_user u ON u.id = l.created_by
            WHERE l.workspace_id = @tenant AND l.kind = @kind AND l.used_at IS NULL AND l.expires_at > now()
            ORDER BY l.created_at DESC;
            """,
            new { tenant, kind = UserLink.Invite },
            cancellationToken: cancellationToken))).AsList();
    }

    /// <summary>Deletes an unused invite of <paramref name="tenant"/>; false when that workspace has no such invite.</summary>
    public async Task<bool> RevokeInviteAsync(Guid tenant, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_link WHERE id = @id AND workspace_id = @tenant AND kind = @kind AND used_at IS NULL;",
            new { id, tenant, kind = UserLink.Invite },
            cancellationToken: cancellationToken)) == 1;
    }

    /// <summary>The workspace and role a usable invite grants; null for any token that is not one.</summary>
    public async Task<InviteInfo?> DescribeInviteAsync(string token, CancellationToken cancellationToken)
    {
        if (!LinkToken.Plausible(token))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<InviteInfo>(new CommandDefinition(
            """
            SELECT w.id AS "WorkspaceId", w.slug AS "Slug", l.role AS "Role", l.expires_at AS "ExpiresAt"
            FROM user_link l
            JOIN workspace w ON w.id = l.workspace_id
            WHERE l.token_hash = @hash AND l.kind = @kind AND l.used_at IS NULL AND l.expires_at > now();
            """,
            new { hash = LinkToken.Hash(token), kind = UserLink.Invite },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Uses the invite: for <paramref name="signedIn"/>, a membership of its workspace (an invite never lowers a
    /// role they already hold there); otherwise a new user named <paramref name="name"/> with that membership. The
    /// name and password are already judged (<see cref="AccountRules"/>). One transaction: a name already taken
    /// rolls the use back, so the link still works for a second try.
    /// </summary>
    public async Task<InviteOutcome> AcceptInviteAsync(
        string token,
        DashboardUser? signedIn,
        string? name,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!LinkToken.Plausible(token))
        {
            return new InviteOutcome(InviteOutcomeKind.Invalid);
        }

        // Before the transaction: a hash takes as long as a sign-in does, and no row needs to wait on it.
        var hash = signedIn is null ? Hasher.HashPassword(Nobody, password!) : null;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var used = await connection.QuerySingleOrDefaultAsync<UsedInvite>(new CommandDefinition(
            """
            UPDATE user_link SET used_at = now()
            WHERE token_hash = @hash AND kind = @kind AND used_at IS NULL AND expires_at > now()
            RETURNING workspace_id AS "WorkspaceId", role AS "Role";
            """,
            new { hash = LinkToken.Hash(token), kind = UserLink.Invite },
            transaction,
            cancellationToken: cancellationToken));

        if (used is null)
        {
            return new InviteOutcome(InviteOutcomeKind.Invalid);
        }

        var user = signedIn;
        if (user is null)
        {
            user = new DashboardUser(Guid.CreateVersion7(), name!.Trim(), IsSystemAdmin: false);
            var created = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO app_user (id, name, password_hash) VALUES (@Id, @Name, @hash)
                ON CONFLICT ((lower(name))) DO NOTHING;
                """,
                new { user.Id, user.Name, hash },
                transaction,
                cancellationToken: cancellationToken));

            if (created == 0)
            {
                return new InviteOutcome(InviteOutcomeKind.NameTaken);
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tenant_membership (user_id, workspace_id, role) VALUES (@user, @WorkspaceId, @Role)
            ON CONFLICT (user_id, workspace_id) DO UPDATE
               SET role = CASE WHEN excluded.role = @admin THEN excluded.role ELSE tenant_membership.role END;
            """,
            new { user = user.Id, used.WorkspaceId, used.Role, admin = TenantMembership.Admin },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new InviteOutcome(InviteOutcomeKind.Accepted, user, used.WorkspaceId);
    }
}

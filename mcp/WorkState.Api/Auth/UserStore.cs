using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Domain;

namespace WorkState.Api.Auth;

public sealed record DashboardUser(Guid Id, string Name, bool IsSystemAdmin);

/// <summary>A cookie's user as the table has them now, with the security stamp a live cookie must carry.</summary>
public sealed record DashboardSession(DashboardUser User, Guid Stamp);

/// <summary>The user an OAuth token acts as, in the one tenant it acts in.</summary>
public sealed record OAuthUser(DashboardUser User, Guid Tenant);

/// <summary>A workspace a user may enter, and why: their membership's role, or <see cref="SystemAdmin"/>.</summary>
public sealed record UserTenant(Guid Id, string Slug, string Role)
{
    public const string SystemAdmin = "system-admin";
}

/// <summary>
/// The dashboard's users (<c>app_user</c>, <c>tenant_membership</c>). Passwords are kept only as
/// <see cref="PasswordHasher{TUser}"/> hashes (C64).
/// <para>
/// The configured <see cref="DashboardOptions"/> credential is consulted exactly once in a database's life: the
/// first sign-in that presents it while no user exists creates the first user from it — at sign-in rather than
/// at startup, so the stored hash is of a password someone actually typed and a server nobody signs in to stores
/// nothing. From then on only the table decides, and editing the configured password changes nothing.
/// </para>
/// </summary>
public sealed class UserStore(NpgsqlDataSource dataSource)
{
    private readonly WorkStateConnections _connections = new(dataSource);

    private static readonly PasswordHasher<AppUser> Hasher = new();

    private static readonly AppUser Nobody = new();

    /// <summary>What an unknown name is verified against, so it costs what a wrong password does.</summary>
    private static readonly Lazy<string> DummyHash = new(
        () => Hasher.HashPassword(Nobody, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private sealed record Row(Guid Id, string Name, string PasswordHash, bool IsSystemAdmin, bool Disabled);

    private sealed record SessionRow(Guid Id, string Name, bool IsSystemAdmin, Guid SecurityStamp);

    /// <summary>
    /// The user these credentials sign in as, or null. A wrong password, an unknown name and a disabled user
    /// are the same null, and each runs one hash verification.
    /// </summary>
    public async Task<DashboardUser?> SignInAsync(
        string? name,
        string? password,
        DashboardOptions dashboard,
        CancellationToken cancellationToken)
    {
        if (dashboard.Matches(name, password) && !await AnyAsync(cancellationToken))
        {
            var first = await BootstrapAsync(dashboard.Username!, password!, cancellationToken);
            if (first is not null)
            {
                return first;
            }
        }

        return await VerifyAsync(name, password ?? string.Empty, cancellationToken);
    }

    /// <summary>
    /// The user a cookie names, while they still exist, are not disabled, and may enter
    /// <paramref name="tenant"/>: a member of it, or a system administrator while it exists.
    /// </summary>
    public async Task<DashboardUser?> EnterAsync(Guid id, Guid tenant, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<DashboardUser>(new CommandDefinition(
            """
            SELECT u.id AS "Id", u.name AS "Name", u.is_system_admin AS "IsSystemAdmin"
            FROM app_user u
            JOIN workspace w ON w.id = @tenant
            WHERE u.id = @id AND u.disabled_at IS NULL
              AND (u.is_system_admin
                   OR EXISTS (SELECT 1 FROM tenant_membership m WHERE m.user_id = u.id AND m.workspace_id = w.id));
            """,
            new { id, tenant },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// <see cref="EnterAsync"/> with the user's security stamp, for the cookie's per-request check: a cookie
    /// whose stamp is not this one was issued before a password change or reset and is signed out.
    /// </summary>
    public async Task<DashboardSession?> SessionAsync(Guid id, Guid tenant, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            SELECT u.id AS "Id", u.name AS "Name", u.is_system_admin AS "IsSystemAdmin",
                   u.security_stamp AS "SecurityStamp"
            FROM app_user u
            JOIN workspace w ON w.id = @tenant
            WHERE u.id = @id AND u.disabled_at IS NULL
              AND (u.is_system_admin
                   OR EXISTS (SELECT 1 FROM tenant_membership m WHERE m.user_id = u.id AND m.workspace_id = w.id));
            """,
            new { id, tenant },
            cancellationToken: cancellationToken));

        return row is null ? null : new DashboardSession(new DashboardUser(row.Id, row.Name, row.IsSystemAdmin), row.SecurityStamp);
    }

    /// <summary>The stamp a cookie issued to <paramref name="id"/> now carries; null for a user who is gone.</summary>
    public async Task<Guid?> StampAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT security_stamp FROM app_user WHERE id = @id;",
            new { id },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Sets a new password when <paramref name="current"/> is the user's password now, and draws a new security
    /// stamp, which signs out every cookie but the one the caller re-issues with it. Null for a wrong current
    /// password, or a user gone or disabled since; each runs one hash verification, as a sign-in does. The new
    /// password is already judged (<see cref="AccountRules"/>).
    /// </summary>
    public async Task<Guid?> ChangePasswordAsync(Guid id, string current, string next, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        var hash = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT password_hash FROM app_user WHERE id = @id AND disabled_at IS NULL;",
            new { id },
            cancellationToken: cancellationToken));

        if (Hasher.VerifyHashedPassword(Nobody, hash ?? DummyHash.Value, current) == PasswordVerificationResult.Failed
            || hash is null)
        {
            return null;
        }

        // Only over the hash that was verified: of two changes racing, the second finds it gone and is refused.
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            UPDATE app_user SET password_hash = @next, security_stamp = gen_random_uuid()
            WHERE id = @id AND password_hash = @hash AND disabled_at IS NULL
            RETURNING security_stamp;
            """,
            new { id, hash, next = Hasher.HashPassword(Nobody, next) },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Where a sign-in lands: the user's first membership, else, for a system administrator, the oldest
    /// workspace. Null for a user who is a member of nothing.
    /// </summary>
    public async Task<Guid?> HomeTenantAsync(DashboardUser user, CancellationToken cancellationToken)
        => (await TenantsAsync(user, cancellationToken)).FirstOrDefault()?.Id;

    /// <summary>
    /// The workspaces <paramref name="user"/> may enter, memberships first in the order they were granted,
    /// then — for a system administrator — every other workspace, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<UserTenant>> TenantsAsync(DashboardUser user, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        return (await connection.QueryAsync<UserTenant>(new CommandDefinition(
            """
            SELECT w.id AS "Id", w.slug AS "Slug", coalesce(m.role, @systemAdmin) AS "Role"
            FROM workspace w
            LEFT JOIN tenant_membership m ON m.workspace_id = w.id AND m.user_id = @Id
            WHERE m.user_id IS NOT NULL OR @IsSystemAdmin
            ORDER BY m.created_at NULLS LAST, w.created_at, w.slug;
            """,
            new { user.Id, user.IsSystemAdmin, systemAdmin = UserTenant.SystemAdmin },
            cancellationToken: cancellationToken))).AsList();
    }

    /// <summary>
    /// Who an OAuth token acts as, and in which tenant. A token that names a tenant names its user by id and
    /// keeps both while that user may still enter it. One that names none — every token issued before tokens
    /// named a tenant — names its user by name and takes their only membership. Otherwise the refusal says what
    /// to do. Asked on every use and every refresh, so a removed membership or a disabled user ends the token
    /// at its next call.
    /// </summary>
    public async Task<(OAuthUser? Grant, string? Refusal)> OAuthUserAsync(
        string subject,
        Guid? claimed,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        if (claimed is { } tenant)
        {
            if (!Guid.TryParse(subject, out var id)
                || await connection.QuerySingleOrDefaultAsync<DashboardUser>(new CommandDefinition(
                    """
                    SELECT id AS "Id", name AS "Name", is_system_admin AS "IsSystemAdmin"
                    FROM app_user
                    WHERE id = @id AND disabled_at IS NULL;
                    """,
                    new { id },
                    cancellationToken: cancellationToken)) is not { } named)
            {
                return (null, "The token's user no longer signs in to this server. Sign in again.");
            }

            return await EnterAsync(named.Id, tenant, cancellationToken) is { } entered
                ? (new OAuthUser(entered, tenant), null)
                : (null, $"{named.Name} may no longer enter the workspace this token was issued for. Sign in again.");
        }

        var user = await connection.QuerySingleOrDefaultAsync<DashboardUser>(new CommandDefinition(
            """
            SELECT id AS "Id", name AS "Name", is_system_admin AS "IsSystemAdmin"
            FROM app_user
            WHERE lower(name) = lower(@subject) AND disabled_at IS NULL;
            """,
            new { subject },
            cancellationToken: cancellationToken));

        if (user is null)
        {
            return (null, $"The token's user \"{subject}\" no longer signs in to this server. Sign in again.");
        }

        var memberships = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT workspace_id FROM tenant_membership WHERE user_id = @Id LIMIT 2;",
            new { user.Id },
            cancellationToken: cancellationToken))).AsList();

        return memberships switch
        {
            [var only] => (new OAuthUser(user, only), null),
            [] => (null, $"This token names no workspace, and {user.Name} is a member of none. A system administrator "
                         + "adds the membership; then sign in again."),
            _ => (null, $"This token names no workspace, and {user.Name} is a member of several, so it cannot be "
                        + "told which one to act in. Sign in again to get a token for one workspace."),
        };
    }

    /// <summary>
    /// Whether <paramref name="user"/> manages everything in <paramref name="tenant"/> rather than only what
    /// they started: an admin of it, or a system administrator, and not disabled.
    /// </summary>
    public async Task<bool> ManagesAsync(Guid user, Guid tenant, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1 FROM app_user u
                WHERE u.id = @user AND u.disabled_at IS NULL
                  AND (u.is_system_admin
                       OR EXISTS (SELECT 1 FROM tenant_membership m
                                  WHERE m.user_id = u.id AND m.workspace_id = @tenant AND m.role = @admin)));
            """,
            new { user, tenant, admin = TenantMembership.Admin },
            cancellationToken: cancellationToken));
    }

    private async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        return await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM app_user);");
    }

    /// <summary>
    /// The first user: system administrator, and admin of every workspace that exists — of a new
    /// <c>default</c> one on a fresh install, so they have a tenant to sign in to. Null when another sign-in
    /// created a user first; the caller then verifies against that one like any other.
    /// </summary>
    private async Task<DashboardUser?> BootstrapAsync(string name, string password, CancellationToken cancellationToken)
    {
        var hash = Hasher.HashPassword(Nobody, password);

        await using var bypass = await _connections.OwnerBypassTransactionAsync(Bypass.Users, cancellationToken);
        var (connection, transaction) = bypass;

        // SHARE ROW EXCLUSIVE conflicts with itself: two first sign-ins queue here, and the second finds the
        // first one's user below and creates none.
        await connection.ExecuteAsync("LOCK TABLE app_user IN SHARE ROW EXCLUSIVE MODE;", transaction: transaction);

        if (await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM app_user);", transaction: transaction))
        {
            return null;
        }

        var user = new DashboardUser(Guid.CreateVersion7(), name, IsSystemAdmin: true);
        await connection.ExecuteAsync(
            """
            INSERT INTO app_user (id, name, password_hash, is_system_admin)
            VALUES (@Id, @Name, @hash, @IsSystemAdmin);

            INSERT INTO workspace (id, slug)
            SELECT @workspace, 'default' WHERE NOT EXISTS (SELECT 1 FROM workspace)
            ON CONFLICT (slug) DO NOTHING;

            INSERT INTO tenant_membership (user_id, workspace_id, role)
            SELECT @Id, id, @role FROM workspace;
            """,
            new { user.Id, user.Name, hash, user.IsSystemAdmin, role = TenantMembership.Admin, workspace = Guid.CreateVersion7() },
            transaction);

        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private async Task<DashboardUser?> VerifyAsync(string? name, string password, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OwnerBypassAsync(Bypass.Users, cancellationToken);
        var row = string.IsNullOrEmpty(name)
            ? null
            : await connection.QuerySingleOrDefaultAsync<Row>(
                """
                SELECT id AS "Id", name AS "Name", password_hash AS "PasswordHash",
                       is_system_admin AS "IsSystemAdmin", disabled_at IS NOT NULL AS "Disabled"
                FROM app_user
                WHERE lower(name) = lower(@name);
                """,
                new { name });

        var result = Hasher.VerifyHashedPassword(Nobody, row?.PasswordHash ?? DummyHash.Value, password);
        if (row is null || row.Disabled || result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await connection.ExecuteAsync(
                "UPDATE app_user SET password_hash = @hash WHERE id = @Id;",
                new { hash = Hasher.HashPassword(Nobody, password), row.Id });
        }

        return new DashboardUser(row.Id, row.Name, row.IsSystemAdmin);
    }
}

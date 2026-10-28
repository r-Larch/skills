using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using WorkState.Core.Domain;

namespace WorkState.Api.Auth;

public sealed record DashboardUser(Guid Id, string Name, bool IsSystemAdmin);

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
    private static readonly PasswordHasher<AppUser> Hasher = new();

    private static readonly AppUser Nobody = new();

    /// <summary>What an unknown name is verified against, so it costs what a wrong password does.</summary>
    private static readonly Lazy<string> DummyHash = new(
        () => Hasher.HashPassword(Nobody, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private sealed record Row(Guid Id, string Name, string PasswordHash, bool IsSystemAdmin, bool Disabled);

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

    /// <summary>The user a cookie names, while they still exist and are not disabled.</summary>
    public async Task<DashboardUser?> FindActiveAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<DashboardUser>(
            """
            SELECT id AS "Id", name AS "Name", is_system_admin AS "IsSystemAdmin"
            FROM app_user
            WHERE id = @id AND disabled_at IS NULL;
            """,
            new { id });
    }

    private async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM app_user);");
    }

    /// <summary>
    /// The first user: system administrator, and admin of every workspace that exists. Null when another
    /// sign-in created a user first; the caller then verifies against that one like any other.
    /// </summary>
    private async Task<DashboardUser?> BootstrapAsync(string name, string password, CancellationToken cancellationToken)
    {
        var hash = Hasher.HashPassword(Nobody, password);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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

            INSERT INTO tenant_membership (user_id, workspace_id, role)
            SELECT @Id, id, @role FROM workspace;
            """,
            new { user.Id, user.Name, hash, user.IsSystemAdmin, role = TenantMembership.Admin },
            transaction);

        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private async Task<DashboardUser?> VerifyAsync(string? name, string password, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
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

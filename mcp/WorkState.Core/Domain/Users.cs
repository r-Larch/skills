namespace WorkState.Core.Domain;

/// <summary>
/// A person who signs in to the dashboard. <c>app_user</c>, because <c>user</c> is reserved in Postgres.
/// The name is unique ignoring case (an expression index the model cannot state, in the Users
/// migration). Read and written with Dapper by <c>UserStore</c>; the entity exists so the migration is
/// generated from the model.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>ASP.NET Core's <c>PasswordHasher</c> format; never the password.</summary>
    public string PasswordHash { get; set; } = "";

    public bool IsSystemAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set means the user can no longer sign in, and a cookie they hold stops working.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>
    /// Every dashboard cookie carries the value it was issued with, and a cookie whose value is no longer this one
    /// is signed out. A password change or reset draws a new one, which is what ends the user's other sessions.
    /// </summary>
    public Guid SecurityStamp { get; set; }
}

/// <summary>A user's role in one workspace (tenant).</summary>
public class TenantMembership
{
    public const string Admin = "admin";
    public const string Member = "member";

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary><see cref="Admin"/> or <see cref="Member"/>.</summary>
    public string Role { get; set; } = Member;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A single-use link an administrator copies to someone: an <see cref="Invite"/> into a workspace with a role,
/// or a <see cref="Reset"/> of one user's password. Only the SHA-256 of its token is kept (C64); the token is
/// in the create response and nowhere else.
/// </summary>
public class UserLink
{
    public const string Invite = "invite";
    public const string Reset = "reset";

    public Guid Id { get; set; }

    /// <summary><see cref="Invite"/> or <see cref="Reset"/>.</summary>
    public string Kind { get; set; } = Invite;

    /// <summary>Lowercase hex SHA-256 of the token's UTF-8 bytes.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>The workspace an invite joins; null on a reset.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>The <see cref="TenantMembership.Role"/> an invite grants; null on a reset.</summary>
    public string? Role { get; set; }

    /// <summary>The user a reset is for; null on an invite.</summary>
    public Guid? UserId { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set once, by the one request that used it.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}

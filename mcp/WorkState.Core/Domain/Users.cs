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

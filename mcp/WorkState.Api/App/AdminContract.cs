namespace WorkState.Api.App;

/// <summary><c>GET /api/session/me</c>: who the cookie is, and whether the admin pages are theirs to open.</summary>
public sealed record SessionUserView(Guid Id, string Name, bool SystemAdmin, bool Administers);

/// <summary>A workspace the caller administers. <c>Current</c> is the one their cookie acts in.</summary>
public sealed record AdminTenantView(
    Guid Id,
    string Slug,
    DateTimeOffset CreatedAt,
    int Members,
    int Admins,
    int PendingInvites,
    bool Current);

public sealed record AdminMemberView(
    Guid UserId,
    string Name,
    string Role,
    bool SystemAdmin,
    DateTimeOffset JoinedAt,
    DateTimeOffset? DisabledAt);

public sealed record AdminUserTenantView(Guid Id, string Slug, string Role);

public sealed record AdminUserView(
    Guid Id,
    string Name,
    bool SystemAdmin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    IReadOnlyList<AdminUserTenantView> Tenants);

public sealed record PendingInviteView(Guid Id, string Role, string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The one response that carries the token: it is not stored, so it cannot be shown again.</summary>
public sealed record CreatedInviteView(Guid Id, string Token, string Url, string Role, string Tenant, DateTimeOffset ExpiresAt);

/// <summary>The one response that carries a reset token: only its hash is kept.</summary>
public sealed record CreatedResetView(Guid UserId, string Name, string Token, string Url, DateTimeOffset ExpiresAt);

/// <summary><c>GET /api/reset/{token}</c>: whose password the link sets, and until when it works.</summary>
public sealed record ResetView(string Name, DateTimeOffset ExpiresAt);

public sealed record ResetDoneView(string Name);

public sealed record MemberRoleView(Guid UserId, string Role, bool Changed);

public sealed record UserStateView(Guid Id, DateTimeOffset? DisabledAt);

/// <summary><c>GET /api/invite/{token}</c>: what the link joins, and as whom it would be accepted right now.</summary>
public sealed record InviteView(Guid TenantId, string Tenant, string Role, DateTimeOffset ExpiresAt, string? SignedInAs, bool Member);

public sealed record InviteAcceptedView(string Name, Guid TenantId);

public sealed record CreateTenantRequest(string? Slug);

public sealed record RoleRequest(string? Role);

public sealed record AcceptInviteRequest(string? Name, string? Password);

public sealed record ResetRequest(string? Password);

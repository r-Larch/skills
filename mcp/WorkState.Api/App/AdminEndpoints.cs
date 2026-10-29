using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;
using WorkState.Core.Domain;
using WorkState.Mcp;

namespace WorkState.Api.App;

/// <summary>
/// The admin pages' API (doc 08 §4, doc 10 "Outside v1"): workspaces, their members and invites, and — for a
/// system administrator — every user. Cookie only; writes also carry <see cref="ProjectEndpoints.WriteHeader"/>.
/// <para>
/// Unlike the work routes, these name the workspace they act on in the path: administering a tenant is not
/// acting in it, and a system administrator administers every one. So every call is judged against the
/// caller's rights in that workspace as the table has them now (<see cref="AdminStore.RightsAsync"/>), never
/// against the cookie's tenant or anything the request says about the caller. A member gets 403 on every route.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    private const string RightsKey = "workstate:admin-rights";

    public static void MapAdmin(this RouteGroupBuilder api)
    {
        var reads = api.MapGroup("/admin").RequireAuthorization(DashboardAuth.Policy).AddEndpointFilter(RightsFilter);
        var writes = api.MapGroup("/admin").DashboardWrite().AddEndpointFilter(RightsFilter);

        reads.MapGet("/tenants", async (HttpContext context, [FromServices] AdminStore admins) =>
        {
            var current = RequestTenant.Of(context.User);
            var tenants = await admins.TenantsAsync(Rights(context), context.RequestAborted);
            return Results.Ok(tenants.Select(t => View(t, current)));
        });

        writes.MapPost("/tenants", async (CreateTenantRequest request, HttpContext context, [FromServices] AdminStore admins) =>
        {
            var rights = Rights(context);
            if (!rights.IsSystemAdmin)
            {
                return Error(StatusCodes.Status403Forbidden, $"Only a system administrator creates workspaces, and {rights.Name} is not one.");
            }

            var slug = request.Slug?.Trim();
            if (AccountRules.SlugProblem(slug) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem);
            }

            return await admins.CreateTenantAsync(slug!, context.RequestAborted) is { } created
                ? Results.Created($"/api/admin/tenants/{created.Id}", View(created, RequestTenant.Of(context.User)))
                : Error(StatusCodes.Status409Conflict, $"A workspace named {slug} already exists; choose another slug.");
        });

        reads.MapGet("/tenants/{id:guid}/members", async (Guid id, HttpContext context, [FromServices] AdminStore admins) =>
        {
            if (await TenantRefusalAsync(context, admins, id) is { } refused)
            {
                return refused;
            }

            var members = await admins.MembersAsync(id, context.RequestAborted);
            return Results.Ok(members.Select(m => new AdminMemberView(m.UserId, m.Name, m.Role, m.IsSystemAdmin, m.JoinedAt, m.DisabledAt)));
        });

        writes.MapPut("/tenants/{id:guid}/members/{user:guid}", async (
            Guid id,
            Guid user,
            RoleRequest request,
            HttpContext context,
            [FromServices] AdminStore admins) =>
        {
            if (await TenantRefusalAsync(context, admins, id) is { } refused)
            {
                return refused;
            }

            if (!KnownRole(request.Role))
            {
                return Error(StatusCodes.Status400BadRequest, RoleProblem);
            }

            return await admins.SetRoleAsync(id, user, request.Role!, context.RequestAborted) switch
            {
                MemberChange.NotMember => NotMember(id),
                MemberChange.LastAdmin => LastAdmin(id),
                var change => Results.Ok(new MemberRoleView(user, request.Role!, change == MemberChange.Changed)),
            };
        });

        writes.MapDelete("/tenants/{id:guid}/members/{user:guid}", async (
            Guid id,
            Guid user,
            HttpContext context,
            [FromServices] AdminStore admins) =>
        {
            if (await TenantRefusalAsync(context, admins, id) is { } refused)
            {
                return refused;
            }

            var removal = await admins.RemoveAsync(id, user, context.RequestAborted);
            return removal.Change switch
            {
                MemberChange.NotMember => NotMember(id),
                MemberChange.LastAdmin => LastAdmin(id),
                MemberChange.LastProjectMember => LastProjectMember(removal.SoleMemberOf),
                _ => Results.NoContent(),
            };
        });

        reads.MapGet("/tenants/{id:guid}/invites", async (
            Guid id,
            HttpContext context,
            [FromServices] AdminStore admins,
            [FromServices] UserLinkStore links) =>
        {
            if (await TenantRefusalAsync(context, admins, id) is { } refused)
            {
                return refused;
            }

            var pending = await links.PendingInvitesAsync(id, context.RequestAborted);
            return Results.Ok(pending.Select(i => new PendingInviteView(i.Id, i.Role, i.CreatedBy, i.CreatedAt, i.ExpiresAt)));
        });

        writes.MapPost("/tenants/{id:guid}/invites", async (
            Guid id,
            RoleRequest request,
            HttpContext context,
            [FromServices] AdminStore admins,
            [FromServices] UserLinkStore links,
            [FromServices] WebBase web) =>
        {
            if (await TenantRefusalAsync(context, admins, id) is { } refused)
            {
                return refused;
            }

            if (!KnownRole(request.Role))
            {
                return Error(StatusCodes.Status400BadRequest, RoleProblem);
            }

            var slug = await admins.SlugAsync(id, context.RequestAborted);
            var created = await links.InviteAsync(id, request.Role!, Rights(context).UserId, context.RequestAborted);

            return Results.Created(
                $"/api/admin/tenants/{id}/invites/{created.Id}",
                new CreatedInviteView(created.Id, created.Token, $"{web.Current}/join/{created.Token}", request.Role!, slug!, created.ExpiresAt));
        });

        writes.MapDelete("/tenants/{id:guid}/invites/{invite:guid}", async (
            Guid id,
            Guid invite,
            HttpContext context,
            [FromServices] AdminStore admins,
            [FromServices] UserLinkStore links) =>
        {
            if (await TenantRefusalAsync(context, admins, id) is { } refused)
            {
                return refused;
            }

            return await links.RevokeInviteAsync(id, invite, context.RequestAborted)
                ? Results.NoContent()
                : Error(
                    StatusCodes.Status404NotFound,
                    $"That workspace has no unused invite {invite}; GET /api/admin/tenants/{id}/invites lists the pending ones.");
        });

        reads.MapGet("/users", async (HttpContext context, [FromServices] AdminStore admins) =>
        {
            if (SystemAdminRefusal(Rights(context), "lists every user") is { } refused)
            {
                return refused;
            }

            var users = await admins.UsersAsync(context.RequestAborted);
            return Results.Ok(users.Select(u => new AdminUserView(
                u.Id,
                u.Name,
                u.IsSystemAdmin,
                u.CreatedAt,
                u.DisabledAt,
                [.. u.Tenants.Select(t => new AdminUserTenantView(t.Id, t.Slug, t.Role))])));
        });

        writes.MapPost("/users/{id:guid}/reset-link", async (
            Guid id,
            HttpContext context,
            [FromServices] AdminStore admins,
            [FromServices] UserLinkStore links,
            [FromServices] WebBase web) =>
        {
            var rights = Rights(context);
            var target = await admins.UserAsync(id, context.RequestAborted);
            if (ResetRefusal(rights, target) is { } refused)
            {
                return refused;
            }

            var created = await links.ResetAsync(id, rights.UserId, context.RequestAborted);
            return Results.Created(
                $"/api/admin/users/{id}",
                new CreatedResetView(id, target!.Name, created.Token, $"{web.Current}/reset/{created.Token}", created.ExpiresAt));
        });

        writes.MapPost("/users/{id:guid}/disable", (Guid id, HttpContext context, [FromServices] AdminStore admins)
            => SetDisabledAsync(id, true, context, admins));

        writes.MapPost("/users/{id:guid}/enable", (Guid id, HttpContext context, [FromServices] AdminStore admins)
            => SetDisabledAsync(id, false, context, admins));
    }

    /// <summary>The caller's rights, read once per request; 403 for anyone who administers nothing.</summary>
    private static async ValueTask<object?> RightsFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var admins = http.RequestServices.GetRequiredService<AdminStore>();
        var rights = Guid.TryParse(http.User.FindFirstValue(DashboardAuth.UserIdClaim), out var user)
            ? await admins.RightsAsync(user, http.RequestAborted)
            : null;

        if (rights is not { Any: true })
        {
            return Error(
                StatusCodes.Status403Forbidden,
                $"{http.User.Identity?.Name ?? "This user"} administers no workspace: the admin pages are for a workspace's "
                + "admins and for system administrators.");
        }

        http.Items[RightsKey] = rights;
        return await next(context);
    }

    private static AdminRights Rights(HttpContext context) => (AdminRights)context.Items[RightsKey]!;

    /// <summary>
    /// 403 unless the caller manages <paramref name="tenant"/> — asked before whether it exists, so a tenant admin
    /// learns nothing about workspaces that are not theirs — then 404 for a system administrator's unknown id.
    /// </summary>
    private static async Task<IResult?> TenantRefusalAsync(HttpContext context, AdminStore admins, Guid tenant)
    {
        var rights = Rights(context);
        if (!rights.Manages(tenant))
        {
            return Error(
                StatusCodes.Status403Forbidden,
                $"{rights.Name} is not an admin of that workspace; GET /api/admin/tenants lists the ones they administer.");
        }

        return await admins.SlugAsync(tenant, context.RequestAborted) is not null
            ? null
            : Error(StatusCodes.Status404NotFound, $"No workspace has the id {tenant}; GET /api/admin/tenants lists them.");
    }

    private static IResult? SystemAdminRefusal(AdminRights rights, string what)
        => rights.IsSystemAdmin
            ? null
            : Error(StatusCodes.Status403Forbidden, $"Only a system administrator {what}, and {rights.Name} is not one.");

    /// <summary>
    /// A reset link hands its holder the whole account, every workspace of it. So a system administrator may make
    /// one for anyone, and a tenant admin only for a user who is no system administrator and every one of whose
    /// workspaces they administer — never one with a foot in a workspace that is not theirs (C63). Every refusal,
    /// an unknown id included, is the same 403, so it says nothing about the user's other workspaces.
    /// </summary>
    private static IResult? ResetRefusal(AdminRights rights, AdminUser? target)
    {
        if (rights.IsSystemAdmin)
        {
            return target is null
                ? Error(StatusCodes.Status404NotFound, "No user has that id; GET /api/admin/users lists them.")
                : null;
        }

        return target is { IsSystemAdmin: false, Tenants.Count: > 0 } && target.Tenants.All(t => rights.Administers.Contains(t.Id))
            ? null
            : Error(
                StatusCodes.Status403Forbidden,
                $"{rights.Name} may create a reset link only for a user every one of whose workspaces they administer: "
                + "a reset hands over the whole account. Ask a system administrator to create this one.");
    }

    private static async Task<IResult> SetDisabledAsync(Guid id, bool disabled, HttpContext context, AdminStore admins)
    {
        var rights = Rights(context);
        if (SystemAdminRefusal(rights, disabled ? "disables users" : "enables users") is { } refused)
        {
            return refused;
        }

        if (disabled && id == rights.UserId)
        {
            return Error(
                StatusCodes.Status409Conflict,
                "A system administrator cannot disable themselves; another system administrator can.");
        }

        var (found, disabledAt) = await admins.SetDisabledAsync(id, disabled, context.RequestAborted);
        return found
            ? Results.Ok(new UserStateView(id, disabledAt))
            : Error(StatusCodes.Status404NotFound, $"No user has the id {id}; GET /api/admin/users lists them.");
    }

    private static AdminTenantView View(AdminTenant t, Guid? current)
        => new(t.Id, t.Slug, t.CreatedAt, t.Members, t.Admins, t.PendingInvites, t.Id == current);

    private const string RoleProblem = "The role is \"admin\" or \"member\". Every member has full work rights; an admin also manages the workspace's members and invites.";

    private static bool KnownRole(string? role) => role is TenantMembership.Admin or TenantMembership.Member;

    private static IResult NotMember(Guid tenant)
        => Error(
            StatusCodes.Status404NotFound,
            $"That user is not a member of this workspace; GET /api/admin/tenants/{tenant}/members lists its members.");

    private static IResult LastAdmin(Guid tenant)
        => Error(
            StatusCodes.Status409Conflict,
            "That is this workspace's last admin, and a workspace keeps at least one. Make another member an admin first "
            + $"(PUT /api/admin/tenants/{tenant}/members/{{user}} with {{\"role\": \"admin\"}}).");

    private static IResult LastProjectMember(IReadOnlyList<string> projects)
        => Error(
            StatusCodes.Status409Conflict,
            $"That user is the last member of {(projects.Count == 1 ? "the project" : "the projects")} {string.Join(", ", projects)}, "
            + "and a project keeps at least one. Add another member to each first (POST /api/projects/{id}/members with "
            + "{\"userId\": …} or {\"all\": true}), then remove the user from the workspace.");

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

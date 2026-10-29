using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Mcp;

namespace WorkState.Api.App;

public sealed record ProjectMemberView(Guid UserId, string Name, DateTimeOffset AddedAt, string? AddedBy, DateTimeOffset? DisabledAt);

public sealed record ProjectCandidateView(Guid UserId, string Name, DateTimeOffset? DisabledAt);

/// <summary>A project's members and the workspace's users who could be added. <c>Everyone</c>: none are left.</summary>
public sealed record ProjectMembersView(
    Guid ProjectId,
    string Ref,
    bool Everyone,
    IReadOnlyList<ProjectMemberView> Members,
    IReadOnlyList<ProjectCandidateView> Candidates);

/// <summary><c>{userId}</c> adds one member of the workspace, <c>{all: true}</c> every one.</summary>
public sealed record ProjectMemberRequest(Guid? UserId, bool? All);

/// <summary>
/// The sharing API (doc 10, "Outside v1"; doc 02 §12b). Cookie only, writes with
/// <see cref="ProjectEndpoints.WriteHeader"/>, in the cookie's workspace and no other (C62). A member of the
/// project may share it, and so may an admin of the workspace — a system administrator is one of every
/// workspace. Anyone else gets the same 404 as for an id that is no project, so a private project's existence
/// is not revealed.
/// </summary>
public static class ProjectMemberEndpoints
{
    public static void MapProjectMembers(this RouteGroupBuilder api)
    {
        var reads = api.MapGroup("/projects").RequireAuthorization(DashboardAuth.Policy);
        var writes = api.MapGroup("/projects").DashboardWrite();

        reads.MapGet("/{id:guid}/members", async (Guid id, HttpContext context, [FromServices] WorkWriter writer,
            [FromServices] AdminStore admins, [FromServices] RequestTenant tenant) =>
        {
            if (await CallerAsync(context, admins, tenant) is not { } caller)
            {
                return NotFound(id);
            }

            return await writer.ProjectMembersAsync(tenant.Id, id, caller.User, caller.Administers, context.RequestAborted) is { } found
                ? Results.Ok(View(found))
                : NotFound(id);
        });

        writes.MapPost("/{id:guid}/members", async (Guid id, ProjectMemberRequest request, HttpContext context,
            [FromServices] WorkWriter writer, [FromServices] AdminStore admins, [FromServices] RequestTenant tenant) =>
        {
            if (await CallerAsync(context, admins, tenant) is not { } caller)
            {
                return NotFound(id);
            }

            var everyone = request.All == true;
            if (everyone == (request.UserId is not null))
            {
                return Error(
                    StatusCodes.Status400BadRequest,
                    "Send {\"userId\": \"…\"} to add one member of the workspace, or {\"all\": true} to add every one; "
                    + $"GET /api/projects/{id}/members lists who can be added.");
            }

            var outcome = await writer.AddProjectMemberAsync(
                tenant.Id, Actor(context, caller.User), id, caller.User, caller.Administers, request.UserId, everyone,
                context.RequestAborted);

            return outcome.Change switch
            {
                MembershipChange.NotFound => NotFound(id),
                MembershipChange.NotInTenant => Error(
                    StatusCodes.Status404NotFound,
                    $"No member of this workspace has the id {request.UserId}; only a workspace's members can join its "
                    + $"projects, and GET /api/projects/{id}/members lists them as candidates."),
                _ => Results.Ok(View(outcome.Membership!)),
            };
        });

        writes.MapDelete("/{id:guid}/members/{user:guid}", async (Guid id, Guid user, HttpContext context,
            [FromServices] WorkWriter writer, [FromServices] AdminStore admins, [FromServices] RequestTenant tenant) =>
        {
            if (await CallerAsync(context, admins, tenant) is not { } caller)
            {
                return NotFound(id);
            }

            var outcome = await writer.RemoveProjectMemberAsync(
                tenant.Id, Actor(context, caller.User), id, caller.User, caller.Administers, user, context.RequestAborted);

            return outcome.Change switch
            {
                MembershipChange.NotFound => NotFound(id),
                MembershipChange.NotMember => Error(
                    StatusCodes.Status404NotFound,
                    $"That user is not a member of this project; GET /api/projects/{id}/members lists its members."),
                MembershipChange.LastMember => Error(
                    StatusCodes.Status409Conflict,
                    "That is this project's last member, and a project keeps at least one. Add another member first "
                    + $"(POST /api/projects/{id}/members with {{\"userId\": …}}), then remove this one."),
                _ => Results.NoContent(),
            };
        });
    }

    private sealed record Caller(Guid User, bool Administers);

    /// <summary>The cookie's user and whether they administer the cookie's workspace, as the table has it now.</summary>
    private static async Task<Caller?> CallerAsync(HttpContext context, AdminStore admins, RequestTenant tenant)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(DashboardAuth.UserIdClaim), out var user))
        {
            return null;
        }

        var rights = await admins.RightsAsync(user, context.RequestAborted);
        return rights is null ? null : new Caller(user, rights.Manages(tenant.Id));
    }

    private static Actor Actor(HttpContext context, Guid user)
        => new(context.User.Identity?.Name ?? DashboardAuth.Role, ActorRole.Human) { UserId = user };

    private static ProjectMembersView View(ProjectMembership found)
        => new(
            found.ProjectId,
            found.Ref,
            found.Candidates.Count == 0,
            [.. found.Members.Select(m => new ProjectMemberView(m.UserId, m.Name, m.AddedAt, m.AddedBy, m.DisabledAt))],
            [.. found.Candidates.Select(c => new ProjectCandidateView(c.UserId, c.Name, c.DisabledAt))]);

    private static IResult NotFound(Guid id)
        => Error(
            StatusCodes.Status404NotFound,
            $"No project you can share has the id {id}: it is not in this workspace, or you are not one of its members "
            + "and not an admin of the workspace. A member of the project, or a workspace admin, can add you.");

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

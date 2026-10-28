using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Mcp;

namespace WorkState.Api.App;

public sealed record ArchiveResponse(Guid Id, string Ref, DateTimeOffset? ArchivedAt, bool Changed);

/// <summary>
/// The dashboard's one write: archiving a project (doc 10, "Outside v1"). Under <c>/api</c> and not
/// <c>/api/v1</c>, which is read-only by rule. Cookie only, like <c>/api/session</c>; a bearer token
/// is never evaluated here, so an agent archives through <c>work_update(archived:)</c> instead.
/// </summary>
public static class ProjectEndpoints
{
    /// <summary>
    /// The cross-site guard. A form post cannot set a header, and a cross-origin <c>fetch</c> that
    /// sets one is preflighted, which this host answers without CORS — so a request carrying it came
    /// from the dashboard's own origin. The cookie is already <c>SameSite=Strict</c>; this does not
    /// lean on the browser honouring that.
    /// </summary>
    public const string WriteHeader = "X-WorkState-Write";

    public static void MapProjectArchive(this RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/projects").DashboardWrite();

        projects.MapPost("/{id:guid}/archive", (Guid id, HttpContext context, [FromServices] WorkWriter writer,
                [FromServices] RequestTenant tenant, CancellationToken cancellationToken)
            => SetAsync(id, true, context, writer, tenant, cancellationToken));

        projects.MapPost("/{id:guid}/unarchive", (Guid id, HttpContext context, [FromServices] WorkWriter writer,
                [FromServices] RequestTenant tenant, CancellationToken cancellationToken)
            => SetAsync(id, false, context, writer, tenant, cancellationToken));
    }

    /// <summary>Cookie only, and only with <see cref="WriteHeader"/>: what every dashboard write requires.</summary>
    public static RouteGroupBuilder DashboardWrite(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(DashboardAuth.Policy)
            .AddEndpointFilter(async (context, next) =>
                string.IsNullOrWhiteSpace(context.HttpContext.Request.Headers[WriteHeader])
                    ? Error(StatusCodes.Status403Forbidden, $"a dashboard write needs the {WriteHeader} header")
                    : await next(context));

        return group;
    }

    private static async Task<IResult> SetAsync(
        Guid id,
        bool archived,
        HttpContext context,
        WorkWriter writer,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var actor = new Actor(context.User.Identity?.Name ?? DashboardAuth.Role, ActorRole.Human);
        var outcome = await writer.SetArchivedAsync(tenant.Id, actor, id, archived, cancellationToken);

        return outcome switch
        {
            null => Error(StatusCodes.Status404NotFound, $"no project with id {id}"),
            { Kind: not "project" } => Error(
                StatusCodes.Status400BadRequest,
                $"{outcome.Ref} is a {outcome.Kind}; only a project is archived"),
            _ => Results.Ok(new ArchiveResponse(outcome.Id, outcome.Ref, outcome.ArchivedAt, outcome.Changed)),
        };
    }

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

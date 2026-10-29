using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WorkState.Api.App.V1;
using WorkState.Api.Auth;
using WorkState.Mcp;

namespace WorkState.Api.App;

/// <summary>
/// The human's side of <c>/api</c>: signing in, the live stream, and the v1 read API (doc 10). The
/// reads reuse the queries the MCP tools use; nothing here writes SQL of its own and nothing
/// re-renders. If the dashboard and an agent ever show different numbers, the dashboard is lying
/// about what the agent sees.
/// </summary>
public static class AppEndpoints
{
    public static void MapAppApi(this WebApplication app)
    {
        // ExcludeFromDescription keeps the operator's API out of the service's OpenAPI document
        // (doc 08 §4): that document describes /mcp to agents, and an endpoint an agent can never
        // reach does not belong in it.
        var api = app.MapGroup("/api").ExcludeFromDescription();

        MapSession(api);
        api.MapAdmin();
        api.MapInvites();
        api.MapResets();
        api.MapAgentSessions();
        api.MapProjectArchive();
        api.MapProjectMembers();
        api.MapSessionRelease();

        // doc 10 rule 5: the stream and the v1 reads take a cookie, an agent token or a reader token.
        api.MapGet("/stream", EventStream.HandleAsync).RequireAuthorization(ApiV1.Policy);
        api.MapApiV1();
    }

    private static void MapSession(RouteGroupBuilder api)
    {
        // Signing in cannot require being signed in.
        api.MapPost("/session", async (
            SessionRequest request,
            DashboardOptions dashboard,
            [FromServices] UserStore? users,
            HttpContext context) =>
        {
            if (ApiV1.IsReader(context.User))
            {
                return Results.Forbid();
            }

            // Without a store there is no user table to sign in against or to create the first user in.
            var user = users is null
                ? null
                : await users.SignInAsync(request.Username, request.Password, dashboard, context.RequestAborted);

            if (user is null)
            {
                return Results.Unauthorized();
            }

            // A user in several tenants lands in their first and switches with POST /api/session/tenant.
            if (await users!.HomeTenantAsync(user, context.RequestAborted) is not { } tenant)
            {
                return Error(
                    StatusCodes.Status403Forbidden,
                    $"{user.Name} is not a member of any workspace; a system administrator adds the membership");
            }

            await DashboardAuth.SignInAsync(context, user, tenant);

            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(RateLimits.SignIn);

        // Whether the cookie is there, for a page that must know before it asks: the consent flow's sign-in
        // page continues at once when it is, which is also what carries a SameSite=Strict cookie past a
        // cross-site arrival (see Connect.tsx).
        api.MapGet("/session", () => Results.NoContent()).RequireAuthorization(DashboardAuth.Policy);

        // Who the cookie is, for the shell: the account menu's name and whether the Admin page is shown. The
        // admin API judges every call itself; this only decides what the sidebar offers.
        api.MapGet("/session/me", async (HttpContext context, [FromServices] AdminStore admins) =>
        {
            var signedIn = SignedIn(context.User);
            var rights = await admins.RightsAsync(signedIn.Id, context.RequestAborted);
            return Results.Ok(new SessionUserView(signedIn.Id, signedIn.Name, signedIn.IsSystemAdmin, rights?.Any ?? false));
        }).RequireAuthorization(DashboardAuth.Policy);

        api.MapGet("/session/tenants", async (HttpContext context, [FromServices] UserStore users) =>
        {
            var current = RequestTenant.Of(context.User);
            var tenants = await users.TenantsAsync(SignedIn(context.User), context.RequestAborted);
            return Results.Ok(tenants.Select(t => new SessionTenantView(t.Id, t.Slug, t.Role, t.Id == current)));
        }).RequireAuthorization(DashboardAuth.Policy);

        // Re-issues the cookie in another tenant. A dashboard write: the cookie plus the write header.
        api.MapGroup("/session/tenant").DashboardWrite().MapPost("", async (
            SwitchTenantRequest request,
            HttpContext context,
            [FromServices] UserStore users) =>
        {
            var signedIn = SignedIn(context.User);
            if (request.Id is not { } id || await users.EnterAsync(signedIn.Id, id, context.RequestAborted) is not { } user)
            {
                return Error(
                    StatusCodes.Status403Forbidden,
                    $"{signedIn.Name} may not enter that workspace; GET /api/session/tenants lists the ones they may");
            }

            await DashboardAuth.SignInAsync(context, user, id);
            return Results.NoContent();
        });

        // A dashboard write that guesses at the current password as a sign-in does: a wrong one is sign-in's bare
        // 401, and it is limited like sign-in, in a window of its own. Success ends every other cookie of the user
        // (a new security stamp) and re-issues this one with it; OAuth tokens and minted sessions stay.
        api.MapGroup("/session/password").DashboardWrite().MapPost("", async (
            ChangePasswordRequest request,
            HttpContext context,
            [FromServices] UserStore users) =>
        {
            if (AccountRules.PasswordProblem(request.Next) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem);
            }

            var signedIn = SignedIn(context.User);
            if (await users.ChangePasswordAsync(signedIn.Id, request.Current ?? string.Empty, request.Next!, context.RequestAborted)
                is not { } stamp)
            {
                return Results.Unauthorized();
            }

            await context.SignInAsync(
                DashboardAuth.SchemeName,
                DashboardAuth.Principal(signedIn, RequestTenant.Of(context.User)!.Value, stamp));
            return Results.NoContent();
        }).RequireRateLimiting(RateLimits.Password);

        api.MapDelete("/session", async (HttpContext context) =>
        {
            if (ApiV1.IsReader(context.User))
            {
                return Results.Forbid();
            }

            await context.SignOutAsync(DashboardAuth.SchemeName);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    /// <summary>The cookie's user, as <see cref="DashboardAuth"/> re-read it on this request.</summary>
    private static DashboardUser SignedIn(ClaimsPrincipal user)
        => new(
            Guid.Parse(user.FindFirstValue(DashboardAuth.UserIdClaim)!),
            user.Identity?.Name ?? string.Empty,
            user.FindFirstValue(DashboardAuth.SystemAdminClaim) == "true");

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

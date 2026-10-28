using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;

namespace WorkState.Api.App;

/// <summary>
/// The page an invite link opens (<c>/join/{token}</c>) reads and uses the link here. No session is needed —
/// the token is the credential — but one is honoured: a signed-in user accepts as themselves and gains the
/// membership; anyone else names a new user and its password. Rate-limited per client like signing in, since
/// both guess at a secret.
/// </summary>
public static class InviteEndpoints
{
    private const string Unusable =
        "This invite link cannot be used: it was used already, revoked, or it expired. Ask an admin of the workspace for a new one.";

    public static void MapInvites(this RouteGroupBuilder api)
    {
        api.MapGet("/invite/{token}", async (
            string token,
            HttpContext context,
            [FromServices] UserLinkStore links,
            [FromServices] UserStore users) =>
        {
            if (await links.DescribeInviteAsync(token, context.RequestAborted) is not { } invite)
            {
                return Error(StatusCodes.Status404NotFound, Unusable);
            }

            var signedIn = await SignedInAsync(context);
            var member = signedIn is not null
                         && (await users.TenantsAsync(signedIn, context.RequestAborted))
                         .Any(t => t.Id == invite.WorkspaceId && t.Role != UserTenant.SystemAdmin);

            return Results.Ok(new InviteView(invite.WorkspaceId, invite.Slug, invite.Role, invite.ExpiresAt, signedIn?.Name, member));
        }).AllowAnonymous().RequireRateLimiting(RateLimits.Link);

        api.MapPost("/invite/{token}/accept", async (
            string token,
            AcceptInviteRequest? request,
            HttpContext context,
            [FromServices] UserLinkStore links) =>
        {
            var signedIn = await SignedInAsync(context);
            if (signedIn is null
                && (AccountRules.NameProblem(request?.Name) ?? AccountRules.PasswordProblem(request?.Password)) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem);
            }

            var outcome = await links.AcceptInviteAsync(token, signedIn, request?.Name, request?.Password, context.RequestAborted);
            switch (outcome.Kind)
            {
                case InviteOutcomeKind.Invalid:
                    return Error(StatusCodes.Status404NotFound, Unusable);
                case InviteOutcomeKind.NameTaken:
                    return Error(
                        StatusCodes.Status409Conflict,
                        $"The name {request!.Name!.Trim()} is taken. If it is yours, sign in first and open this link again; "
                        + "otherwise choose another name.");
            }

            await context.SignInAsync(DashboardAuth.SchemeName, DashboardAuth.Principal(outcome.User!, outcome.Tenant));
            return Results.Ok(new InviteAcceptedView(outcome.User!.Name, outcome.Tenant));
        }).AllowAnonymous().RequireWriteHeader().RequireRateLimiting(RateLimits.Link);
    }

    /// <summary>
    /// The dashboard cookie's user, if the request carries a live one. These routes take no policy, so the
    /// cookie scheme is asked here — which runs its per-request re-check like any cookie route.
    /// </summary>
    private static async Task<DashboardUser?> SignedInAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(DashboardAuth.SchemeName);
        return result.Succeeded
               && Guid.TryParse(result.Principal.FindFirstValue(DashboardAuth.UserIdClaim), out var id)
            ? new DashboardUser(
                id,
                result.Principal.Identity?.Name ?? string.Empty,
                result.Principal.FindFirstValue(DashboardAuth.SystemAdminClaim) == "true")
            : null;
    }

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

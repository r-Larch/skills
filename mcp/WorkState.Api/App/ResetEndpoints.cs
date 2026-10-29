using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;

namespace WorkState.Api.App;

/// <summary>
/// The page a reset link opens (<c>/reset/{token}</c>) reads and uses the link here, with no session: the token
/// is the credential. Using it sets the password and signs out every cookie of that user, the browser's own
/// included, so the next step is signing in with the new password. Rate-limited per client like an invite.
/// </summary>
public static class ResetEndpoints
{
    private const string Unusable =
        "This reset link cannot be used: it was used already, replaced by a newer one, or it expired. Ask an admin for a new one.";

    public static void MapResets(this RouteGroupBuilder api)
    {
        api.MapGet("/reset/{token}", async (string token, HttpContext context, [FromServices] UserLinkStore links) =>
            await links.DescribeResetAsync(token, context.RequestAborted) is { } reset
                ? Results.Ok(new ResetView(reset.Name, reset.ExpiresAt))
                : Error(StatusCodes.Status404NotFound, Unusable))
            .AllowAnonymous().RequireRateLimiting(RateLimits.Link);

        api.MapPost("/reset/{token}", async (
            string token,
            ResetRequest? request,
            HttpContext context,
            [FromServices] UserLinkStore links) =>
        {
            if (AccountRules.PasswordProblem(request?.Password) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem);
            }

            return await links.UseResetAsync(token, request!.Password!, context.RequestAborted) is { } name
                ? Results.Ok(new ResetDoneView(name))
                : Error(StatusCodes.Status404NotFound, Unusable);
        }).AllowAnonymous().RequireWriteHeader().RequireRateLimiting(RateLimits.Link);
    }

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

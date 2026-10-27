using Microsoft.AspNetCore.Authentication;
using WorkState.Api.App.V1;
using WorkState.Api.Auth;

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
        api.MapAgentSessions();
        api.MapProjectArchive();

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
            HttpContext context) =>
        {
            if (ApiV1.IsReader(context.User))
            {
                return Results.Forbid();
            }

            if (!dashboard.Matches(request.Username, request.Password))
            {
                return Results.Unauthorized();
            }

            await context.SignInAsync(
                DashboardAuth.SchemeName,
                DashboardAuth.Principal(request.Username!));

            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(RateLimits.SignIn);

        // Whether the cookie is there, for a page that must know before it asks: the consent flow's sign-in
        // page continues at once when it is, which is also what carries a SameSite=Strict cookie past a
        // cross-site arrival (see Connect.tsx).
        api.MapGet("/session", () => Results.NoContent()).RequireAuthorization(DashboardAuth.Policy);

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
}

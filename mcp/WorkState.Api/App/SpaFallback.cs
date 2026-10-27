namespace WorkState.Api.App;

/// <summary>
/// The SPA's entry point, and the answer when there isn't one yet.
/// <para>
/// <c>wwwroot/</c> is gitignored — it is the frontend's build output, so it belongs to whoever ran
/// the build rather than to the repository. A clone without Node therefore has no
/// <c>index.html</c> at all, and a 404 on the one URL a human would type is a worse answer than a
/// sentence saying what is and is not built. The sentence lives here rather than in a committed
/// file for the same reason: a file under <c>wwwroot/</c> would work on the machine that wrote it
/// and be missing everywhere else, and the SPA build would have to fight it.
/// </para>
/// </summary>
internal static class SpaFallback
{
    private const string Placeholder =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>WorkState</title>
        </head>
        <body>
        <p>The WorkState dashboard is not built yet; the read API it uses is live at
        <code>/api/v1</code> (sign in with <code>POST /api/session</code>, then
        <code>/api/v1/overview</code>, <code>/api/v1/runs/{id}/board</code>,
        <code>/api/v1/items/{id}</code>, <code>/api/v1/inbox</code>, <code>/api/v1/journal</code> and
        <code>/api/stream</code>), and agents reach <code>/mcp</code> on this same host with a
        bearer token.</p>
        </body>
        </html>

        """;

    /// <summary>
    /// Registered last, and that ordering is load-bearing: this answers anything unmatched, so
    /// registered before <c>/mcp</c> or <c>/api</c> it would serve HTML to an agent's tool call and
    /// to every dashboard request, with a 200 and no hint of what went wrong.
    /// </summary>
    public static void MapSpaFallback(this WebApplication app)
    {
        app.UseStaticFiles();

        app.MapFallback(async (HttpContext context) =>
        {
            // A fallback that answers everything answers /api/typo and /mcp/typo too, with a 200 and
            // a page of HTML — which is how a client discovers a route it got wrong only by parsing
            // the SPA. Those two prefixes belong to the two APIs, so an unmatched path under them is
            // a 404 rather than the shell.
            if (Claimed(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await ServeAsync(context);
        }).AllowAnonymous();
    }

    /// <summary>The SPA's shell, for a route that judged the request itself before handing it over.</summary>
    public static async Task ServeAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";

        var index = context.RequestServices.GetRequiredService<IWebHostEnvironment>()
            .WebRootFileProvider.GetFileInfo("index.html");

        if (index.Exists)
        {
            await context.Response.SendFileAsync(index, context.RequestAborted);
            return;
        }

        await context.Response.WriteAsync(Placeholder, context.RequestAborted);
    }

    private static bool Claimed(PathString path)
        => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
}

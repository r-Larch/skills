using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using WorkState.Api.Auth;
using WorkState.Core.Board;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Mcp;

namespace WorkState.Api.App;

/// <summary>
/// <c>/r/{path}</c>: the address a <c>web</c> line prints. The path is resolved by the reader that
/// resolves <c>work_get</c>'s refs, so any form an agent can call is a form a human can open, and the
/// answer is a redirect to the SPA page for that item — the only place its guid appears.
/// <para>
/// Cookie only, judged here rather than by a policy: a policy's challenge is the API's bare 401, and a
/// browser following a link needs a way to sign in. That page posts to <c>/api/session</c> and reloads
/// this same URL, so the link survives the sign-in without the target or a credential in any URL, and
/// nothing about the store is said before the cookie is.
/// </para>
/// </summary>
internal static class RefLinks
{
    public static void MapRefLinks(this WebApplication app)
        => app.MapGet("/r/{**path}", RedirectAsync).AllowAnonymous().ExcludeFromDescription();

    private static async Task<IResult> RedirectAsync(
        string? path,
        HttpContext context,
        BoardReader reader,
        BoardQueries board,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        var signedIn = await context.AuthenticateAsync(DashboardAuth.SchemeName);
        if (!signedIn.Succeeded || !signedIn.Principal.IsInRole(DashboardAuth.Role))
        {
            return Html(StatusCodes.Status401Unauthorized, SignIn);
        }

        var asked = (path ?? string.Empty).Trim('/');
        if (asked.Length == 0)
        {
            return Results.Redirect("/");
        }

        switch (await reader.ResolveAsync(workspace.Slug, asked, cancellationToken))
        {
            case ScopeResult.Found found:
                return Results.Redirect(await PageAsync(board, found.Item, cancellationToken));

            case ScopeResult.Ambiguous ambiguous:
                return Html(
                    StatusCodes.Status300MultipleChoices,
                    Page($"<p><code>{Encode(asked)}</code> names {ambiguous.Matches} items. Pick one:</p>", ambiguous.Candidates));

            case ScopeResult.Missing missing:
                return Html(
                    StatusCodes.Status404NotFound,
                    Page($"<p>Nothing has the path <code>{Encode(asked)}</code>.</p>", missing.Nearby));

            default:
                return Html(StatusCodes.Status404NotFound, Page("<p>There is no workspace yet.</p>", []));
        }
    }

    /// <summary>The SPA's own addresses, as <c>frontend/src/nav.ts</c> composes them.</summary>
    private static async Task<string> PageAsync(BoardQueries board, ScopeRef item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case "project":
                return $"/projects/{item.Id}";
            case "run":
                return $"/runs/{item.Id}";
        }

        return await board.RunOfAsync(item.Id, cancellationToken) is { } run && run.Id != item.Id
            ? $"/runs/{run.Id}/{item.Id}"
            : $"/items/{item.Id}";
    }

    private static string Page(string message, IReadOnlyList<string> candidates)
    {
        var body = new StringBuilder(message);
        if (candidates.Count > 0)
        {
            body.Append("\n<ul>\n");
            foreach (var candidate in candidates)
            {
                var href = WebLinks.Link(string.Empty, candidate);
                body.Append($"<li><a href=\"{Encode(href)}\"><code>{Encode(candidate)}</code></a></li>\n");
            }

            body.Append("</ul>");
        }

        body.Append("\n<p><a href=\"/\">Open the dashboard</a></p>");
        return Shell(body.ToString());
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static IResult Html(int status, string html)
        => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);

    private static string Shell(string body)
        => $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>WorkState</title>
            </head>
            <body>
            {body}
            </body>
            </html>

            """;

    private static readonly string SignIn = Shell(
        """
        <form id="sign-in">
        <p>Sign in to the WorkState dashboard to open this link.</p>
        <p><label>Username <input name="username" autocomplete="username"></label></p>
        <p><label>Password <input name="password" type="password" autocomplete="current-password"></label></p>
        <p><button type="submit">Sign in</button> <span id="rejected" hidden>Rejected.</span></p>
        </form>
        <script>
        document.getElementById('sign-in').addEventListener('submit', async (event) => {
          event.preventDefault()
          const form = new FormData(event.target)
          const response = await fetch('/api/session', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username: form.get('username'), password: form.get('password') }),
          })
          if (response.ok) location.reload()
          else document.getElementById('rejected').hidden = false
        })
        </script>
        """);
}

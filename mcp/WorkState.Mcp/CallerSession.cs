using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;

namespace WorkState.Mcp;

/// <summary>
/// doc 11 — resolves which session a call comes from, once per call and before the tool runs, and
/// leaves it on the request for <see cref="CallerIdentity"/>. Resolving touches the row and renews
/// the session's leases, which is why it runs for every tool call and resource read, not only
/// writes. The headers are the only source: the transport is stateless, so neither a connection id
/// nor a declaration made on one call survives to the next.
/// </summary>
public static class CallerSession
{
    internal const string ItemKey = "workstate.session";

    public const string AgentHeader = "X-WorkState-Agent";
    public const string SessionHeader = "X-WorkState-Session";
    public const string LaunchedByHeader = "X-WorkState-Launched-By";
    public const string OriginHeader = "X-WorkState-Origin";
    public const string CwdHeader = "X-WorkState-Cwd";
    public const string ForemanRoleHeader = "X-WorkState-Foreman-Role";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, cancellationToken) =>
        {
            var refused = await ResolveAsync(context, cancellationToken);

            return refused is null
                ? await next(context, cancellationToken)
                : new CallToolResult { Content = [new TextContentBlock { Text = refused }], IsError = true };
        };

    public static McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> ResourceFilter(
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> next)
        => async (context, cancellationToken) =>
        {
            await ResolveAsync(context, cancellationToken);
            return await next(context, cancellationToken);
        };

    private static async Task<string?> ResolveAsync(MessageContext context, CancellationToken cancellationToken)
    {
        var services = context.Services;
        var http = services?.GetService<IHttpContextAccessor>()?.HttpContext;
        var store = services?.GetService<SessionStore>();
        var board = services?.GetService<BoardQueries>();
        var caller = services?.GetService<CallerIdentity>();

        if (http is null || store is null || board is null || caller is null)
        {
            return null;
        }

        var headers = http.Request.Headers;
        string? name = headers[AgentHeader];
        var role = caller.Actor.Role;

        if (string.IsNullOrWhiteSpace(name) || role == ActorRole.System)
        {
            return null;
        }

        var workspace = await board.WorkspaceAsync(services!.GetService<WorkspaceOptions>()?.Slug, cancellationToken);
        if (workspace is not { } workspaceId)
        {
            return null;
        }

        var facts = new SessionFacts(
            Name: name,
            ClaudeSessionId: headers[SessionHeader],
            LaunchedBy: headers[LaunchedByHeader],
            Origin: headers[OriginHeader],
            Cwd: headers[CwdHeader],
            ExternalRole: headers[ForemanRoleHeader],
            ClientName: context.Server.ClientInfo?.Name,
            ClientVersion: context.Server.ClientInfo?.Version);

        var resolved = await store.ResolveAsync(workspaceId, role, facts, cancellationToken);
        if (resolved.Session is { } session)
        {
            http.Items[ItemKey] = session;
        }

        return resolved.Refused;
    }
}

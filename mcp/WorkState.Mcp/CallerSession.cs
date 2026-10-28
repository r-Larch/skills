using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WorkState.Core.Domain;
using WorkState.Core.Writing;

namespace WorkState.Mcp;

/// <summary>
/// doc 11 — resolves which session a call comes from, once per call and before the tool runs, and
/// leaves it on the request for <see cref="CallerIdentity"/>. Resolving touches the row and renews
/// the session's leases, which is why it runs for every tool call and resource read, not only
/// writes. The source is the credential when it is a minted token (doc 14 §2), else the headers:
/// the transport is stateless, so neither a connection id nor a declaration made on one call
/// survives to the next. Order: a minted token's session, ignoring both headers; else
/// <see cref="AgentHeader"/>, binding <see cref="ClientHeader"/> to it when both are sent; else the
/// session <c>work_session</c> bound the client key to; else none.
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

    /// <summary>The session helper's key for the Claude Code process: host/pid/start time.</summary>
    public const string ClientHeader = "X-WorkState-Client";

    /// <summary>
    /// <see cref="ClientHeader"/>, on the principal of a static agent token only — a minted token's
    /// session is its credential's, so its key is never read.
    /// </summary>
    public const string ClientKeyClaim = "workstate:client-key";

    /// <summary>doc 14 §2 — on the principal of a minted token, and only there.</summary>
    public const string SessionIdClaim = "workstate:session-id";

    public const string SessionNameClaim = "workstate:session-name";

    /// <summary>One per grant the minted token carries; a static token never has one.</summary>
    public const string GrantClaim = "workstate:grant";

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
        var caller = services?.GetService<CallerIdentity>();

        if (http is null || store is null || caller is null)
        {
            return null;
        }

        var headers = http.Request.Headers;
        string? name = headers[AgentHeader];
        var role = caller.Actor.Role;

        if (role == ActorRole.System)
        {
            return null;
        }

        // A minted token names its own session; X-WorkState-Agent and the launcher rule do not apply.
        if (Guid.TryParse(http.User.FindFirst(SessionIdClaim)?.Value, out var bound))
        {
            var facts = new SessionFacts(
                ClaudeSessionId: headers[SessionHeader],
                Origin: headers[OriginHeader],
                Cwd: headers[CwdHeader],
                ExternalRole: headers[ForemanRoleHeader],
                ClientName: context.Server.ClientInfo?.Name,
                ClientVersion: context.Server.ClientInfo?.Version);

            if (await store.ResolveBoundAsync(bound, role, facts, cancellationToken) is { } own)
            {
                http.Items[ItemKey] = own;
            }

            return null;
        }

        var key = http.User.FindFirst(ClientKeyClaim)?.Value;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (RequestTenant.Of(http.User) is not { } workspaceId)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var keyed = new SessionFacts(
                ClaudeSessionId: headers[SessionHeader],
                Origin: headers[OriginHeader],
                Cwd: headers[CwdHeader],
                ExternalRole: headers[ForemanRoleHeader],
                ClientName: context.Server.ClientInfo?.Name,
                ClientVersion: context.Server.ClientInfo?.Version,
                ClientKey: key);

            if (await store.ResolveByKeyAsync(workspaceId, role, keyed, cancellationToken) is { } byKey)
            {
                http.Items[ItemKey] = byKey;
            }

            return null;
        }

        var named = new SessionFacts(
            Name: name,
            ClaudeSessionId: headers[SessionHeader],
            LaunchedBy: headers[LaunchedByHeader],
            Origin: headers[OriginHeader],
            Cwd: headers[CwdHeader],
            ExternalRole: headers[ForemanRoleHeader],
            ClientName: context.Server.ClientInfo?.Name,
            ClientVersion: context.Server.ClientInfo?.Version,
            ClientKey: key);

        var resolved = await store.ResolveAsync(workspaceId, role, named, cancellationToken);
        if (resolved.Session is { } session)
        {
            http.Items[ItemKey] = session;
        }

        return resolved.Refused;
    }
}

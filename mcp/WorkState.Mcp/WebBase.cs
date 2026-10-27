using Microsoft.AspNetCore.Http;

namespace WorkState.Mcp;

/// <summary>
/// The scheme and host a <c>web</c> line names: the configured public URL when there is one, else the
/// host this request came in on. <c>X-Forwarded-*</c> is not read — nothing in this pipeline trusts a
/// proxy, so behind one the public URL is set rather than taken from a header any client can send.
/// </summary>
public sealed class WebBase(IHttpContextAccessor accessor, string? publicUrl)
{
    public const string Key = "WorkState:PublicUrl";

    public const string EnvironmentKey = "WORKSTATE_PUBLIC_URL";

    public string? Current
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(publicUrl))
            {
                return publicUrl.Trim().TrimEnd('/');
            }

            return accessor.HttpContext?.Request is { Host.HasValue: true } request
                ? $"{request.Scheme}://{request.Host}{request.PathBase}"
                : null;
        }
    }
}

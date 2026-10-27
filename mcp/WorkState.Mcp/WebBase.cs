using Microsoft.AspNetCore.Http;

namespace WorkState.Mcp;

/// <summary>
/// The scheme and host a <c>web</c> line names: the configured public URL when there is one, else the
/// scheme and host of this request. Behind a proxy listed in <c>WORKSTATE_TRUSTED_PROXIES</c> those are
/// the forwarded ones, because <c>ApiHost</c> runs <c>UseForwardedHeaders</c> first; from any other
/// address <c>X-Forwarded-*</c> is ignored, and with none listed it is never read at all.
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

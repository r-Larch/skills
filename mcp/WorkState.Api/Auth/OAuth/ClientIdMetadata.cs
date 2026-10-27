using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// MCP's client-ID metadata documents: a client whose <c>client_id</c> is an https URL is described by
/// the JSON at that URL, and needs no registration. OpenIddict looks a client up before passthrough, so
/// <see cref="UseClientIdMetadata"/> runs ahead of authentication and leaves the application row behind.
/// <para>
/// The fetch is a request this server makes to a URL anyone can name, so it is the SSRF surface: https
/// only, no redirects, a connection only to a public address (checked at connect time, after DNS, so a
/// rebinding name cannot slip a private address past the check), a size cap, a timeout, and a cache.
/// </para>
/// </summary>
public static class ClientIdMetadata
{
    public const string HttpClientName = "client-id-metadata";

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaximumCacheLifetime = TimeSpan.FromHours(24);

    /// <summary>How long a URL that gave nothing usable is left alone.</summary>
    public static readonly TimeSpan RefusalLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Process-wide, shared by every fetch through <see cref="HttpClientName"/>: a burst of requests naming
    /// fresh URLs cannot fan out without bound.
    /// </summary>
    internal static readonly SemaphoreSlim Fetches = new(8);
    internal static readonly TimeSpan FetchSlotWait = TimeSpan.FromSeconds(2);

    private static readonly string[] Paths = [OAuthServer.AuthorizePath, OAuthServer.TokenPath, OAuthServer.RevokePath];

    public static void Add(IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<ClientIdMetadataResolver>();
        services.AddSingleton<ClientIconResolver>();
        services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = FetchTimeout;
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = ConnectToPublicAddressAsync,
            });
    }

    public static WebApplication UseClientIdMetadata(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (Array.Exists(Paths, path => context.Request.Path.StartsWithSegments(path)))
            {
                var (clientId, redirectUri) = await ParametersAsync(context.Request);
                await context.RequestServices.GetRequiredService<ClientIdMetadataResolver>()
                    .RegisterAsync(clientId, redirectUri, context.RequestAborted);
            }

            await next(context);
        });

        return app;
    }

    public static bool IsMetadataDocument(string? clientId, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || parsed.AbsolutePath is "" or "/"
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6UniqueLocal
                   && !address.IsIPv6Multicast && !address.Equals(IPAddress.IPv6Any);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 10 or 127 => false,
            100 => octets[1] is < 64 or > 127,
            169 => octets[1] != 254,
            172 => octets[1] is < 16 or > 31,
            192 => !(octets[1] == 168 || (octets[1] == 0 && octets[2] is 0 or 2)),
            198 => octets[1] is not (18 or 19),
            >= 224 => false,
            _ => true,
        };
    }

    internal static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        int last;
        while (read < buffer.Length && (last = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += last;
        }

        if (read > maxBytes)
        {
            throw new InvalidDataException($"The response is larger than {maxBytes} bytes.");
        }

        Array.Resize(ref buffer, read);
        return buffer;
    }

    internal static TimeSpan Lifetime(HttpResponseMessage response)
    {
        var declared = response.Headers.CacheControl?.MaxAge ?? DefaultCacheLifetime;
        return declared < MinimumCacheLifetime ? MinimumCacheLifetime
            : declared > MaximumCacheLifetime ? MaximumCacheLifetime
            : declared;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var address = Array.Find(addresses, IsPublic)
                      ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves to no public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<(string? ClientId, string? RedirectUri)> ParametersAsync(HttpRequest request)
    {
        var clientId = request.Query[Parameters.ClientId].FirstOrDefault();
        var redirectUri = request.Query[Parameters.RedirectUri].FirstOrDefault();
        if (!string.IsNullOrEmpty(clientId) || !request.HasFormContentType)
        {
            return (clientId, redirectUri);
        }

        try
        {
            // Buffered by ASP.NET Core, so OpenIddict reads the same form again after this.
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            return (form[Parameters.ClientId].FirstOrDefault(), form[Parameters.RedirectUri].FirstOrDefault());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            return (null, null);
        }
    }
}

public sealed record ClientIdMetadataDocument(
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
    [property: JsonPropertyName("logo_uri")] string? LogoUri = null,
    [property: JsonPropertyName("client_uri")] string? ClientUri = null);

/// <summary>Fetches, checks and caches one document, and writes the application row it describes.</summary>
public sealed class ClientIdMetadataResolver(
    IHttpClientFactory clients,
    IMemoryCache cache,
    IOpenIddictApplicationManager applications,
    ILogger<ClientIdMetadataResolver> logger)
{
    private const int MaxDocumentBytes = 64 * 1024;

    private sealed record Outcome(ClientIdMetadataDocument? Document);

    /// <summary>Never throws: a document that cannot be used leaves no row, and OpenIddict refuses the client.</summary>
    public async Task RegisterAsync(string? clientId, string? redirectUri, CancellationToken cancellationToken)
    {
        if (!ClientIdMetadata.IsMetadataDocument(clientId, out var url))
        {
            return;
        }

        try
        {
            if (await ReadAsync(url, cancellationToken) is { } document)
            {
                await StoreAsync(url, document, redirectUri, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Client ID metadata document {Url} could not be resolved.", url);
        }
    }

    private async Task<ClientIdMetadataDocument?> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var key = "cimd:" + url.OriginalString;
        if (cache.TryGetValue<Outcome>(key, out var cached))
        {
            return cached?.Document;
        }

        if (!await ClientIdMetadata.Fetches.WaitAsync(ClientIdMetadata.FetchSlotWait, cancellationToken))
        {
            logger.LogWarning("Client ID metadata document {Url} not fetched: too many fetches in flight.", url);
            return null;
        }

        try
        {
            using var response = await clients.CreateClient(ClientIdMetadata.HttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var document = response.IsSuccessStatusCode
                ? Parse(await ClientIdMetadata.ReadCappedAsync(response, MaxDocumentBytes, cancellationToken), url)
                : null;

            var lifetime = document is null ? ClientIdMetadata.RefusalLifetime : ClientIdMetadata.Lifetime(response);
            cache.Set(key, new Outcome(document), lifetime);
            return document;
        }
        finally
        {
            ClientIdMetadata.Fetches.Release();
        }
    }

    private ClientIdMetadataDocument? Parse(byte[] body, Uri url)
    {
        ClientIdMetadataDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ClientIdMetadataDocument>(body);
        }
        catch (JsonException)
        {
            document = null;
        }

        // The document must name itself exactly, or one URL could describe a client under another's id.
        if (document is null
            || !string.Equals(url.OriginalString, document.ClientId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.ClientName)
            || Callbacks(document).Length == 0)
        {
            logger.LogWarning("Client ID metadata document {Url} is not a usable client description.", url);
            return null;
        }

        return document;
    }

    private static Uri[] Callbacks(ClientIdMetadataDocument document)
        => (document.RedirectUris ?? [])
            .Select(value => OAuthEndpoints.IsAcceptableRedirect(value, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToArray();

    private async Task StoreAsync(Uri url, ClientIdMetadataDocument document, string? redirectUri, CancellationToken cancellationToken)
    {
        var descriptor = OAuthEndpoints.PublicClient(url.OriginalString, document.ClientName!);
        ClientIcons.Describe(descriptor, document.LogoUri, document.ClientUri);
        foreach (var callback in WithRequestedLoopback(Callbacks(document), redirectUri))
        {
            descriptor.RedirectUris.Add(callback);
        }

        var application = await applications.FindByClientIdAsync(url.OriginalString, cancellationToken);
        if (application is null)
        {
            try
            {
                await applications.CreateAsync(descriptor, cancellationToken);
                return;
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Client {Url} was registered concurrently.", url);
                application = await applications.FindByClientIdAsync(url.OriginalString, cancellationToken)
                              ?? throw new InvalidOperationException($"Client {url} could not be registered.", e);
            }
        }

        var stored = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(stored, application, cancellationToken);
        if (stored.DisplayName == descriptor.DisplayName
            && stored.ClientType == descriptor.ClientType
            && stored.RedirectUris.SetEquals(descriptor.RedirectUris)
            && stored.Permissions.SetEquals(descriptor.Permissions)
            && stored.Requirements.SetEquals(descriptor.Requirements)
            && ClientIcons.SameSources(stored, descriptor))
        {
            return;
        }

        try
        {
            await applications.UpdateAsync(application, descriptor, cancellationToken);
        }
        catch (OpenIddictExceptions.ConcurrencyException e)
        {
            logger.LogDebug(e, "Client {Url} was updated concurrently.", url);
        }
    }

    /// <summary>
    /// RFC 8252 §7.3: a native client's loopback callback may use any port, so a requested loopback URI
    /// that matches a declared one in everything but the port is added for this request.
    /// </summary>
    private static Uri[] WithRequestedLoopback(Uri[] callbacks, string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var requested)
            || !requested.IsLoopback
            || Array.Exists(callbacks, callback => callback.Equals(requested))
            || !Array.Exists(callbacks, callback => callback.IsLoopback
                                                    && callback.Scheme == requested.Scheme
                                                    && callback.Host == requested.Host
                                                    && callback.AbsolutePath == requested.AbsolutePath))
        {
            return callbacks;
        }

        return [.. callbacks, requested];
    }
}

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenIddict.Abstractions;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// The connecting client's icon on the consent page, served from this origin so the browser never asks a
/// third party about the client. Where it comes from is what the client said about itself — its
/// <c>logo_uri</c>, else the <c>/favicon.ico</c> of its <c>client_uri</c>'s origin — kept on the application
/// row as properties when it registers or names a metadata document.
/// <para>
/// The fetch is the same SSRF surface as <see cref="ClientIdMetadata"/> and goes through its client: https
/// only, a public address checked at connect time, no redirects, a timeout, the shared fetch slots. On top:
/// a size cap and raster image types only — SVG is a document that can carry script.
/// </para>
/// </summary>
public static class ClientIcons
{
    public const string LogoUri = "logo_uri";
    public const string ClientUri = "client_uri";

    private const int MaxUriLength = 2048;

    /// <summary>Records the sources a client declared; one that is not a plain https URL is dropped, not refused.</summary>
    public static void Describe(OpenIddictApplicationDescriptor descriptor, string? logoUri, string? clientUri)
    {
        if (Https(logoUri) is { } logo)
        {
            descriptor.Properties[LogoUri] = JsonSerializer.SerializeToElement(logo.AbsoluteUri);
        }

        if (Https(clientUri) is { } client)
        {
            descriptor.Properties[ClientUri] = JsonSerializer.SerializeToElement(client.AbsoluteUri);
        }
    }

    public static string? Declared(OpenIddictApplicationDescriptor descriptor, string name) => Read(descriptor.Properties, name);

    public static bool SameSources(OpenIddictApplicationDescriptor stored, OpenIddictApplicationDescriptor described)
        => Declared(stored, LogoUri) == Declared(described, LogoUri)
           && Declared(stored, ClientUri) == Declared(described, ClientUri);

    /// <summary>The URLs to try, in order.</summary>
    public static async Task<Uri[]> CandidatesAsync(
        IOpenIddictApplicationManager applications,
        object application,
        CancellationToken cancellationToken)
    {
        var properties = await applications.GetPropertiesAsync(application, cancellationToken);

        var candidates = new List<Uri>();
        if (Https(Read(properties, LogoUri)) is { } logo)
        {
            candidates.Add(logo);
        }

        if (Https(Read(properties, ClientUri)) is { } client)
        {
            candidates.Add(new Uri(new Uri(client.GetLeftPart(UriPartial.Authority)), "/favicon.ico"));
        }

        return [.. candidates];
    }

    private static string? Read(IReadOnlyDictionary<string, JsonElement> properties, string name)
        => properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? Https(string? value)
        => value is { Length: > 0 and <= MaxUriLength }
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;
}

public sealed record ClientIcon(byte[] Body, string ContentType);

/// <summary>Fetches and caches one icon per URL, the refusals included.</summary>
public sealed class ClientIconResolver(
    IHttpClientFactory clients,
    IMemoryCache cache,
    ILogger<ClientIconResolver> logger)
{
    public const int MaxIconBytes = 64 * 1024;

    private static readonly string[] ImageTypes =
        ["image/png", "image/x-icon", "image/vnd.microsoft.icon", "image/jpeg", "image/gif", "image/webp"];

    private sealed record Outcome(ClientIcon? Icon);

    public async Task<ClientIcon?> ResolveAsync(
        IOpenIddictApplicationManager applications,
        object application,
        CancellationToken cancellationToken)
    {
        foreach (var url in await ClientIcons.CandidatesAsync(applications, application, cancellationToken))
        {
            if (await FetchAsync(url, cancellationToken) is { } icon)
            {
                return icon;
            }
        }

        return null;
    }

    private async Task<ClientIcon?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        var key = "client-icon:" + url.AbsoluteUri;
        if (cache.TryGetValue<Outcome>(key, out var cached))
        {
            return cached?.Icon;
        }

        if (!await ClientIdMetadata.Fetches.WaitAsync(ClientIdMetadata.FetchSlotWait, cancellationToken))
        {
            logger.LogWarning("Client icon {Url} not fetched: too many fetches in flight.", url);
            return null;
        }

        try
        {
            ClientIcon? icon = null;
            var lifetime = ClientIdMetadata.RefusalLifetime;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await clients.CreateClient(ClientIdMetadata.HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (response.IsSuccessStatusCode && type is not null && ImageTypes.Contains(type))
                {
                    var body = await ClientIdMetadata.ReadCappedAsync(response, MaxIconBytes, cancellationToken);
                    if (body.Length > 0)
                    {
                        icon = new ClientIcon(body, type);
                        lifetime = ClientIdMetadata.Lifetime(response);
                    }
                }
                else
                {
                    logger.LogInformation("Client icon {Url} refused: {Status} {Type}.", url, (int)response.StatusCode, type);
                }
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException
                                          || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogInformation(e, "Client icon {Url} could not be fetched.", url);
            }

            cache.Set(key, new Outcome(icon), lifetime);
            return icon;
        }
        finally
        {
            ClientIdMetadata.Fetches.Release();
        }
    }
}

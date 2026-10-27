using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WorkState.Api.Auth.OAuth;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.OAuthTests;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The consent page's client icon: served from this origin, resolved from what the client declared
/// (<c>logo_uri</c>, else <c>client_uri</c>'s favicon), and fetched through the metadata document's
/// SSRF-safe client — so the refusals matter as much as the happy path.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClientIconTests(PostgresFixture fixture)
{
    private const string Callback = "http://127.0.0.1:7777/callback";

    private static readonly byte[] Logo = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    private static readonly byte[] Favicon = [0, 0, 1, 0, 4, 5, 6];

    [Fact]
    public async Task A_metadata_documents_logo_uri_is_preferred_over_its_client_uri_favicon()
    {
        var clientId = $"https://client.example/{Guid.CreateVersion7():n}/client.json";
        var origin = new Origin()
            .Json(clientId, $$"""
                {"client_id":"{{clientId}}","client_name":"Metadata client","redirect_uris":["http://127.0.0.1/callback"],
                 "logo_uri":"https://cdn.client.example/logo.png","client_uri":"https://client.example/about"}
                """)
            .Serve("https://cdn.client.example/logo.png", "image/png", Logo)
            .Serve("https://client.example/favicon.ico", "image/x-icon", Favicon);

        await using var server = await StartAsync(origin);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ClientIdMetadataResolver>().RegisterAsync(clientId, null, Ct);
        }

        var browser = await server.SignInAsync(Ct);
        var icon = await IconAsync(browser, clientId);

        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal(Logo, await icon.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", string.Join(",", icon.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(icon.Headers.CacheControl is { Private: true, MaxAge: not null }, $"Cache-Control: {icon.Headers.CacheControl}");
        Assert.Equal(0, origin.Requests("https://client.example/favicon.ico"));
    }

    [Fact]
    public async Task A_registered_clients_client_uri_favicon_is_used_when_it_has_no_logo_uri_and_is_cached()
    {
        var origin = new Origin().Serve("https://app.example/favicon.ico", "image/vnd.microsoft.icon", Favicon);
        await using var server = await StartAsync(origin);

        var registration = await RegisterAsync(server.NewClient(), new { client_uri = "https://app.example/about/" });
        Assert.Equal("https://app.example/about/", registration.GetProperty("client_uri").GetString());
        var clientId = registration.GetProperty("client_id").GetString()!;

        var browser = await server.SignInAsync(Ct);
        for (var i = 0; i < 2; i++)
        {
            var icon = await IconAsync(browser, clientId);
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
            Assert.Equal(Favicon, await icon.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal("image/vnd.microsoft.icon", icon.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(1, origin.Requests("https://app.example/favicon.ico"));
    }

    [Fact]
    public async Task An_unknown_client_id_is_a_404_with_no_outbound_fetch()
    {
        var origin = new Origin();
        await using var server = await StartAsync(origin);
        var browser = await server.SignInAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await IconAsync(browser, "nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await IconAsync(browser, "https://client.example/unregistered/client.json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("/connect/app-icon", Ct)).StatusCode);
        Assert.Equal(0, origin.Total);
    }

    [Fact]
    public async Task The_icon_needs_the_operators_session()
    {
        await using var server = await StartAsync(new Origin());
        var anonymous = server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);

        var response = await IconAsync(anonymous, clientId);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_logo_on_a_private_address_is_refused_at_connect_time()
    {
        // The real transport, with its connect callback watched: the refusal must come from the public-address
        // check, not from nothing listening there.
        var refused = new ConcurrentBag<string>();
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            $"icon-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Services.PostConfigure<HttpClientFactoryOptions>(
                ClientIdMetadata.HttpClientName,
                options => options.HttpMessageHandlerBuilderActions.Add(handler =>
                {
                    var sockets = (SocketsHttpHandler)handler.PrimaryHandler;
                    var connect = sockets.ConnectCallback!;
                    sockets.ConnectCallback = async (context, cancellationToken) =>
                    {
                        try
                        {
                            return await connect(context, cancellationToken);
                        }
                        catch (HttpRequestException)
                        {
                            refused.Add(context.DnsEndPoint.Host);
                            throw;
                        }
                    };
                })));

        var registration = await RegisterAsync(server.NewClient(), new
        {
            logo_uri = "https://127.0.0.1/logo.png",
            client_uri = "https://169.254.169.254/latest/meta-data/",
        });
        var browser = await server.SignInAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await IconAsync(browser, registration.GetProperty("client_id").GetString()!)).StatusCode);
        Assert.Equal(["127.0.0.1", "169.254.169.254"], refused.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_icon_over_the_size_cap_is_refused()
    {
        var origin = new Origin()
            .Serve("https://big.example/logo.png", "image/png", new byte[ClientIconResolver.MaxIconBytes + 1])
            .Serve("https://fits.example/logo.png", "image/png", new byte[ClientIconResolver.MaxIconBytes]);
        await using var server = await StartAsync(origin);
        var browser = await server.SignInAsync(Ct);

        var big = await RegisterAsync(server.NewClient(), new { logo_uri = "https://big.example/logo.png" });
        Assert.Equal(HttpStatusCode.NotFound, (await IconAsync(browser, big.GetProperty("client_id").GetString()!)).StatusCode);

        var fits = await RegisterAsync(server.NewClient(), new { logo_uri = "https://fits.example/logo.png" });
        var icon = await IconAsync(browser, fits.GetProperty("client_id").GetString()!);
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal(ClientIconResolver.MaxIconBytes, (await icon.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task Only_a_raster_image_type_is_served(string? contentType)
    {
        var origin = new Origin().Serve("https://app.example/logo", contentType, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray());
        await using var server = await StartAsync(origin);

        var registration = await RegisterAsync(server.NewClient(), new { logo_uri = "https://app.example/logo" });
        var browser = await server.SignInAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await IconAsync(browser, registration.GetProperty("client_id").GetString()!)).StatusCode);
        Assert.Equal(1, origin.Requests("https://app.example/logo"));
    }

    [Fact]
    public async Task A_source_that_is_not_https_is_dropped_at_registration()
    {
        var origin = new Origin();
        await using var server = await StartAsync(origin);

        var registration = await RegisterAsync(server.NewClient(), new
        {
            logo_uri = "http://app.example/logo.png",
            client_uri = "file:///etc/passwd",
        });
        Assert.False(registration.TryGetProperty("logo_uri", out _));
        Assert.False(registration.TryGetProperty("client_uri", out _));

        var browser = await server.SignInAsync(Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await IconAsync(browser, registration.GetProperty("client_id").GetString()!)).StatusCode);
        Assert.Equal(0, origin.Total);
    }

    [Fact]
    public async Task The_consent_page_loads_images_from_this_origin_only()
    {
        await using var server = await StartAsync(new Origin());
        var clientId = await OAuthTests.RegisterAsync(server.NewClient());
        var browser = await server.SignInAsync(Ct);

        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), $"{server.BaseAddress}/mcp"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var consent = await browser.GetAsync(authorize.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        Assert.Contains("img-src 'self' data:", string.Join(", ", consent.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    private Task<AppServer> StartAsync(Origin origin)
        => AppServer.StartAsync(
            fixture.ConnectionString,
            $"icon-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Services.PostConfigure<HttpClientFactoryOptions>(
                ClientIdMetadata.HttpClientName,
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = origin)));

    private static Task<HttpResponseMessage> IconAsync(HttpClient browser, string clientId)
        => browser.GetAsync($"/connect/app-icon?client_id={Uri.EscapeDataString(clientId)}", Ct);

    private static async Task<JsonElement> RegisterAsync(HttpClient client, object sources)
    {
        var body = JsonSerializer.SerializeToElement(sources).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.GetString());
        body["client_name"] = "Icon tests";
        body["redirect_uris"] = new[] { Callback };

        var response = await client.PostAsync("/connect/register", AppServer.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    /// <summary>The internet, as far as these tests let the server see it: fixed answers at fixed URLs, counted.</summary>
    private sealed class Origin : HttpMessageHandler
    {
        private readonly Dictionary<string, (string? Type, byte[] Body)> _answers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);

        public int Total => _requests.Values.Sum();

        public int Requests(string url) => _requests.GetValueOrDefault(url);

        public Origin Serve(string url, string? contentType, byte[] body)
        {
            _answers[url] = (contentType, body);
            return this;
        }

        public Origin Json(string url, string json) => Serve(url, "application/json", Encoding.UTF8.GetBytes(json));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.AddOrUpdate(url, 1, (_, count) => count + 1);
            if (!_answers.TryGetValue(url, out var answer))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new ByteArrayContent(answer.Body);
            content.Headers.ContentType = answer.Type is null ? null : new MediaTypeHeaderValue(answer.Type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

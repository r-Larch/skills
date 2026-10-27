using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using OpenIddict.Abstractions;
using WorkState.Api.Auth.OAuth;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// A client whose id is an https URL needs no registration: the document at that URL describes it. The
/// fetch is the server reaching out to a URL anyone can name, so the refusals matter as much as the
/// happy path.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClientIdMetadataTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("https://client.example/oauth/client.json", true)]
    [InlineData("https://client.example/", false)]
    [InlineData("https://client.example", false)]
    [InlineData("http://client.example/oauth/client.json", false)]
    [InlineData("https://client.example/oauth/client.json#x", false)]
    [InlineData("https://user@client.example/oauth/client.json", false)]
    [InlineData("8f2c1a0d", false)]
    [InlineData(null, false)]
    public void Only_an_https_url_with_a_path_is_a_metadata_document(string? clientId, bool expected)
        => Assert.Equal(expected, ClientIdMetadata.IsMetadataDocument(clientId, out _));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    public void Only_a_public_address_is_connected_to(string address, bool expected)
        => Assert.Equal(expected, ClientIdMetadata.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task A_metadata_client_authorizes_with_a_loopback_port_it_did_not_declare()
    {
        var clientId = $"https://client.example/{Guid.CreateVersion7():n}/client.json";
        var document = $$"""
            {"client_id":"{{clientId}}","client_name":"Metadata client","redirect_uris":["http://127.0.0.1/callback"]}
            """;

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            $"cimd-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Services.PostConfigure<HttpClientFactoryOptions>(
                ClientIdMetadata.HttpClientName,
                options => options.HttpMessageHandlerBuilderActions.Add(
                    handler => handler.PrimaryHandler = new Document(clientId, document))));

        var browser = await server.SignInAsync(Ct);
        var pkce = OAuthTests.Pkce.Create();
        const string redirect = "http://127.0.0.1:43123/callback";

        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, $"{server.BaseAddress}/mcp", redirect);
        var tokens = await OAuthTests.ExchangeAsync(server.NewClient(), clientId, code, pkce, redirect);

        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_document_on_a_loopback_host_is_never_fetched()
    {
        // The real transport: localhost resolves to loopback only, so the connect callback refuses it.
        var clientId = $"https://localhost/{Guid.CreateVersion7():n}/client.json";
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, $"cimd-{Guid.CreateVersion7():n}", Ct);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ClientIdMetadataResolver>()
                .RegisterAsync(clientId, null, Ct);

            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            Assert.Null(await applications.FindByClientIdAsync(clientId, Ct));
        }
    }

    [Fact]
    public async Task A_document_that_names_another_client_writes_nothing()
    {
        var clientId = $"https://client.example/{Guid.CreateVersion7():n}/client.json";
        var document = """
            {"client_id":"https://attacker.example/client.json","client_name":"x","redirect_uris":["https://attacker.example/cb"]}
            """;

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            $"cimd-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Services.PostConfigure<HttpClientFactoryOptions>(
                ClientIdMetadata.HttpClientName,
                options => options.HttpMessageHandlerBuilderActions.Add(
                    handler => handler.PrimaryHandler = new Document(clientId, document))));

        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ClientIdMetadataResolver>().RegisterAsync(clientId, null, Ct);

        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        Assert.Null(await applications.FindByClientIdAsync(clientId, Ct));
    }

    /// <summary>Serves one document at one URL; anything else is a 404.</summary>
    private sealed class Document(string url, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri?.OriginalString == url
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

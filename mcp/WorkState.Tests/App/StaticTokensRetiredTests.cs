using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The server deploy (deploy/multi-deploy) configures no static role token: a token string some machine
/// still holds is a credential nobody accepts, while the dashboard and OAuth keep working.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class StaticTokensRetiredTests(PostgresFixture fixture)
{
    [Fact]
    public async Task With_no_static_tokens_configured_a_former_token_reaches_nothing_and_the_cookie_still_works()
    {
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            $"retired-{Guid.CreateVersion7():n}",
            Ct,
            configure: builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkState:Auth:WorkerToken"] = "",
                ["WorkState:Auth:OrchestratorToken"] = "",
                ["WorkState:Auth:ControllerToken"] = "",
            }));

        foreach (var token in new[] { ReadServer.WorkerToken, ReadServer.OrchestratorToken, ReadServer.ControllerToken })
        {
            var client = server.NewClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(AppServer.McpInitialize(), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.PostAsync("/api/agent-sessions", JsonContent.Create(new { name = "retired", role = "worker" }), Ct)).StatusCode);
        }

        // /mcp still points an unauthenticated client at the OAuth server, which is how every client signs in now.
        var anonymous = await server.NewClient().SendAsync(AppServer.McpInitialize(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("resource_metadata", anonymous.Headers.WwwAuthenticate.ToString());

        var signedIn = await server.SignInAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }
}

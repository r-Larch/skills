using System.Net;
using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkState.Api;
using WorkState.Api.Auth;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 08 §4 promises the console is always on. It was not: DataProtection kept the cookie's signing
/// keys inside the container, so every <c>up -d --build</c> recreated the container, discarded the
/// ring and signed the operator out of the thing whose whole claim is that it never goes away.
/// <para>
/// The container dance is not repeatable here, so these assert the piece that is: a second host
/// against the same database honours a cookie the first one issued. Once the container is out of the
/// picture, that is the entirety of "survives a recreate".
/// </para>
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class KeyRingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_dashboard_session_outlives_the_host_that_issued_it()
    {
        var slug = $"keyring-{Guid.CreateVersion7():n}";
        string cookie;

        await using (var first = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct))
        {
            cookie = await SignInForCookieAsync(first);
        }

        // A second host, on a port of its own, with nothing in common with the first but the
        // database — which is what `up -d --build` leaves standing.
        await using var second = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct);

        var client = second.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/watch");
        request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_key_ring_is_a_table_and_a_second_repository_reads_what_the_first_wrote()
    {
        var slug = $"keyring-{Guid.CreateVersion7():n}";

        await using (var server = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct))
        {
            await SignInForCookieAsync(server);

            // Fixed rather than derived from the content root, or the container and a published
            // build on the host would read the same rows under two different purpose chains.
            var options = server.Services.GetRequiredService<IOptions<DataProtectionOptions>>();
            Assert.Equal(ApiHost.DataProtectionApplicationName, options.Value.ApplicationDiscriminator);
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var keys = await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM data_protection_key;", cancellationToken: Ct));
        Assert.True(keys > 0, "signing in should have left a key ring in the database");

        // Two repositories over one database is what two hosts over one database come down to.
        var writer = new PostgresXmlRepository(fixture.DataSource);
        var reader = new PostgresXmlRepository(fixture.DataSource);

        var id = $"round-trip-{Guid.CreateVersion7():n}";
        writer.StoreElement(new XElement("key", new XAttribute("id", id), "ring"), id);

        var readBack = reader.GetAllElements()
            .Where(element => (string?)element.Attribute("id") == id)
            .ToList();

        var only = Assert.Single(readBack);
        Assert.Equal("key", only.Name.LocalName);
        Assert.Equal("ring", only.Value);
    }

    private static async Task<string> SignInForCookieAsync(AppServer server)
    {
        var client = server.NewClient();
        var response = await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith($"{DashboardAuth.CookieName}=", StringComparison.Ordinal));

        return setCookie.Split(';')[0];
    }
}

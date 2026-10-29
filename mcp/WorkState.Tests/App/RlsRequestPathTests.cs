using System.Net;
using Dapper;
using WorkState.Core.Data;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The rest of the suite passing through the real host shows each request's tenant is set right; this shows the
/// request runs as the tenant role at all. The owner would read <c>item</c> without the grant, the role cannot.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RlsRequestPathTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Rls_the_api_request_path_acts_as_the_tenant_role()
    {
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        await using var server = await AppServer.StartAsync(database.ConnectionString, $"rls-{Guid.CreateVersion7():n}", Ct);
        var client = await server.SignInAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);

        await using (var owner = database.Open())
        {
            await owner.ExecuteAsync($"REVOKE SELECT ON item FROM {WorkStateConnections.AppRole};");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);

        await using (var owner = database.Open())
        {
            await owner.ExecuteAsync($"GRANT SELECT ON item TO {WorkStateConnections.AppRole};");
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }
}

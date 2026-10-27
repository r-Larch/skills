using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkState.Core.Data;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>doc 15: the server's own connections run with JIT off; the instance keeps its default.</summary>
[Collection(StoreCollection.Name)]
public sealed class SessionSettingsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_servers_connections_run_with_jit_off_and_the_instance_does_not()
    {
        await using (var plain = new NpgsqlConnection(fixture.ConnectionString))
        {
            Assert.Equal("on", await plain.ExecuteScalarAsync<string>("SHOW jit;"));
        }

        await using (var source = WorkStateDataSource.Create(fixture.ConnectionString))
        await using (var connection = await source.OpenConnectionAsync(Ct))
        {
            Assert.Equal("off", await connection.ExecuteScalarAsync<string>("SHOW jit;"));
        }

        await using var context = fixture.NewContext();
        var efConnection = context.Database.GetDbConnection();
        await efConnection.OpenAsync(Ct);
        Assert.Equal("off", await efConnection.ExecuteScalarAsync<string>("SHOW jit;"));
    }

    [Fact]
    public void An_option_the_caller_already_set_is_kept()
    {
        var settings = new NpgsqlConnectionStringBuilder(
            WorkStateDataSource.WithSessionSettings("Host=h;Database=d;Options=-c statement_timeout=5000"));

        Assert.Equal("-c statement_timeout=5000 -c jit=off", settings.Options);
    }
}

using Dapper;
using Npgsql;
using WorkState.Tests.Store;

namespace WorkState.Tests.App;

/// <summary>
/// An empty database of its own on the fixture's server, for a test that needs <c>app_user</c> empty: the
/// shared database's first user is created by whichever test signs in first. The host migrates it on start.
/// </summary>
internal sealed class FreshDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _name;

    private FreshDatabase(PostgresFixture fixture, string name)
    {
        _fixture = fixture;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static async Task<FreshDatabase> CreateAsync(PostgresFixture fixture, CancellationToken cancellationToken)
    {
        var name = $"users_{Guid.CreateVersion7():n}";
        await using var admin = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
        await admin.ExecuteAsync($"CREATE DATABASE {name};");
        return new FreshDatabase(fixture, name);
    }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = await _fixture.DataSource.OpenConnectionAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_name} WITH (FORCE);");
    }
}

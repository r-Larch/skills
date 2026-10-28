using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using WorkState.Core.Data;
using Xunit;

namespace WorkState.Tests.Store;

/// <summary>
/// A throwaway postgres:17 per test run (CONVENTIONS §2). Not the compose database: the suite has
/// to need nothing seeded and be unable to damage another unit's data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("workstate")
        .WithUsername("workstate")
        .WithPassword("workstate")
        .Build();

    private NpgsqlDataSource? _dataSource;

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("The fixture has not been initialized yet.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);

        await using (var context = NewContext())
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        _dataSource = WorkStateDataSource.Create(ConnectionString);

        // The suite's direct store calls are no request and enter no tenant: on this data source they run as the
        // owner, as before RLS. Every HTTP test goes through the API's own data source, which has no such exception.
        WorkStateConnections.RunUnscopedAsOwner(_dataSource);
    }

    public WorkStateDbContext NewContext()
        => new(WorkStateDbContextOptions.Build(ConnectionString));

    /// <summary>The server log, where auto_explain writes (QueryPlanTests).</summary>
    public async Task<string> ServerLogAsync(CancellationToken cancellationToken)
        => (await _container.GetLogsAsync(timestampsEnabled: false, ct: cancellationToken)).Stderr;

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(StoreCollection.Name)]
public sealed class StoreCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "workstate-store";
}

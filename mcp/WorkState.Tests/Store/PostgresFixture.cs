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

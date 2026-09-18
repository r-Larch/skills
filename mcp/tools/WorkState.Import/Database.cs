using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WorkState.Core.Data;

namespace WorkState.Import;

/// <summary>
/// Where the import writes. A named connection is the normal case; <c>--ephemeral</c> spins a throwaway
/// <c>postgres:17</c> instead, which is how the corpus-wide run is made repeatable on a machine whose
/// other Postgres ports belong to other projects (CONVENTIONS §2) — the importer then cannot reach, and
/// so cannot damage, anything that holds real work.
/// </summary>
public sealed class Database : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;

    private Database(string connectionString, PostgreSqlContainer? container)
    {
        ConnectionString = connectionString;
        _container = container;
    }

    public string ConnectionString { get; }

    public static async Task<Database> OpenAsync(
        string? connectionString,
        bool ephemeral,
        bool migrate,
        CancellationToken cancellationToken = default)
    {
        if (!ephemeral)
        {
            var named = new Database(
                connectionString ?? throw new ArgumentNullException(nameof(connectionString)),
                null);

            if (migrate)
            {
                await named.MigrateAsync(cancellationToken);
            }

            return named;
        }

        var container = new PostgreSqlBuilder("postgres:17")
            .WithDatabase("workstate")
            .WithUsername("workstate")
            .WithPassword("workstate")
            .Build();

        await container.StartAsync(cancellationToken);

        var database = new Database(container.GetConnectionString(), container);
        await database.MigrateAsync(cancellationToken);
        return database;
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = new WorkStateDbContext(WorkStateDbContextOptions.Build(ConnectionString));
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

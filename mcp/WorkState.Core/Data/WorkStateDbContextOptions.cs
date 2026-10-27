using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using WorkState.Core.Domain;

namespace WorkState.Core.Data;

public static class WorkStateDbContextOptions
{
    /// <summary>
    /// Every entry point that opens a <see cref="WorkStateDbContext"/> has to map the same enum set:
    /// Npgsql resolves the native enum OIDs from this registration, and a mismatch is a runtime cast
    /// failure rather than a compile error.
    /// </summary>
    public static void MapEnums(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.MapEnum<ItemStatus>("item_status");
        npgsql.MapEnum<ItemResolution>("item_resolution");
        npgsql.MapEnum<LinkType>("link_type");
        npgsql.MapEnum<AssumptionState>("assumption_state");
        npgsql.MapEnum<EvidenceLevel>("evidence_level");
        npgsql.MapEnum<ReversalCost>("reversal_cost");
        npgsql.MapEnum<NoteKind>("note_kind");
        npgsql.MapEnum<ConstraintKind>("constraint_kind");
        npgsql.MapEnum<ActorRole>("actor_role");
        npgsql.MapEnum<VerifyRunner>("verify_runner");
    }

    public static DbContextOptionsBuilder UseWorkState(
        this DbContextOptionsBuilder builder,
        string connectionString)
        => builder
            .UseNpgsql(WorkStateDataSource.WithSessionSettings(connectionString), MapEnums)
            .UseSnakeCaseNamingConvention();

    public static DbContextOptions<WorkStateDbContext> Build(string connectionString)
        => (DbContextOptions<WorkStateDbContext>)new DbContextOptionsBuilder<WorkStateDbContext>()
            .UseWorkState(connectionString)
            .Options;
}

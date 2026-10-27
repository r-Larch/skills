using Npgsql;
using WorkState.Core.Domain;

namespace WorkState.Core.Data;

/// <summary>
/// The raw connection path used by the hand-written read queries. It has to carry the same enum
/// registrations as <see cref="WorkStateDbContextOptions"/>, or a native enum column reads back as
/// an unmapped type.
/// </summary>
public static class WorkStateDataSource
{
    /// <summary>
    /// The session settings every connection the server opens runs with — this data source and the
    /// EF context alike (<see cref="WorkStateDbContextOptions.UseWorkState"/>). <c>jit=off</c>: the
    /// reads are short and their per-row laterals push the estimate past <c>jit_above_cost</c>, so a
    /// 27 ms board paid 35 ms of compile (doc 15).
    /// </summary>
    public static string WithSessionSettings(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? "-c jit=off" : $"{builder.Options} -c jit=off";
        return builder.ConnectionString;
    }

    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(WithSessionSettings(connectionString));

        builder.MapEnum<ItemStatus>("item_status");
        builder.MapEnum<ItemResolution>("item_resolution");
        builder.MapEnum<LinkType>("link_type");
        builder.MapEnum<AssumptionState>("assumption_state");
        builder.MapEnum<EvidenceLevel>("evidence_level");
        builder.MapEnum<ReversalCost>("reversal_cost");
        builder.MapEnum<NoteKind>("note_kind");
        builder.MapEnum<ConstraintKind>("constraint_kind");
        builder.MapEnum<ActorRole>("actor_role");
        builder.MapEnum<VerifyRunner>("verify_runner");

        return builder.Build();
    }
}

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
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

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

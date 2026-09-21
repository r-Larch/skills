using Microsoft.EntityFrameworkCore;
using WorkState.Core.Data;

namespace WorkState.Api;

/// <summary>
/// Applies the migrations this service's own database needs, at startup, before the first request.
/// <para>
/// FINDINGS-live F-L6: the schema used to exist only as a side effect of
/// <c>WorkState.Import --migrate --root &lt;markdown&gt;</c>, so standing up an empty server meant
/// inventing a corpus directory to import nothing from. doc 06 §4 promises <c>work_brief</c> answers
/// an empty server with onboarding, and a promise that needs an ingest tool to keep is not kept.
/// </para>
/// <para>
/// Single-user, single-writer, one database this service owns: the case where migrating on startup
/// is the right default rather than a hazard. <c>WorkState:Migrate=false</c> turns it off for a
/// deployment that applies migrations out of band.
/// </para>
/// </summary>
public sealed class SchemaOwner(string connectionString)
{
    public void Apply()
    {
        using var db = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString));
        db.Database.Migrate();
    }
}

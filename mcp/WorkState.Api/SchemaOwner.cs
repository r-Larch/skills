using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Migrations;

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
public sealed class SchemaOwner(string connectionString, ILogger<SchemaOwner> logger)
{
    public void Apply()
    {
        using var db = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString));
        db.Database.Migrate();
        Releakproof(db);
    }

    /// <summary>
    /// TenantRls marks <see cref="TenantRls.Leakproof"/> LEAKPROOF, an attribute of built-in functions that
    /// pg_dump does not carry: a database restored from a dump keeps its migration history and loses the flag, and
    /// every subtree read under the tenant role goes back to scanning the workspace (doc 02 §15).
    /// </summary>
    private void Releakproof(WorkStateDbContext db)
    {
        var lost = db.Database
            .SqlQueryRaw<string>(
                """
                SELECT f AS "Value"
                FROM   unnest(@functions) AS f JOIN pg_proc p ON p.oid = to_regprocedure(f)
                WHERE  NOT p.proleakproof;
                """,
                new NpgsqlParameter("functions", TenantRls.Leakproof.ToArray()))
            .ToList();

        if (lost.Count == 0)
        {
            return;
        }

        foreach (var function in lost)
        {
            var sql = "ALTER FUNCTION " + function + " LEAKPROOF;";
            db.Database.ExecuteSqlRaw(sql);
        }

        logger.LogWarning(
            "Re-marked {Functions} LEAKPROOF: the database had lost the flag TenantRls set, as a restore from a dump does.",
            string.Join(", ", lost));
    }
}

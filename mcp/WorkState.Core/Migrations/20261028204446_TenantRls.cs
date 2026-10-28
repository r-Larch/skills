using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Data;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 §15. Row-level security per workspace, as a second line behind every query's own
    /// <c>workspace_id</c> filter: a NOLOGIN role that the request path acts as
    /// (<see cref="WorkStateConnections.TenantAsync"/>), and one policy per tenant table that admits the rows of
    /// <c>app.workspace_id</c> and nothing else. The owner — the image's superuser — is not confined, which is
    /// the bypass <see cref="Bypass"/> names.
    /// </summary>
    public partial class TenantRls : Migration
    {
        /// <summary>
        /// The setting unset, or reset to <c>''</c> once a transaction that set it ended, admits no row. A scalar
        /// subquery, so a plan reads it once (an InitPlan) rather than once per row.
        /// </summary>
        public const string Tenant = $"(SELECT NULLIF(current_setting('{WorkStateConnections.WorkspaceSetting}', true), '')::uuid)";

        public const string Policy = "tenant_isolation";

        /// <summary>Tables that carry their workspace: the policy compares the column.</summary>
        public static readonly IReadOnlyDictionary<string, string> Direct = new Dictionary<string, string>
        {
            ["workspace"] = "id",
            ["item"] = "workspace_id",
            ["item_link"] = "workspace_id",
            ["doc"] = "workspace_id",
            ["event"] = "workspace_id",
            ["assumption"] = "workspace_id",
            ["constraint_rule"] = "workspace_id",
            ["note"] = "workspace_id",
            ["verification"] = "workspace_id",
            ["project_repo"] = "workspace_id",
            ["agent_token"] = "workspace_id",
            ["agent_session"] = "workspace_id",
            ["import_record"] = "workspace_id",
            ["tenant_membership"] = "workspace_id",
            ["user_link"] = "workspace_id",
        };

        /// <summary>
        /// Tables with no workspace of their own: a row is the tenant's when its parent is. No column is added,
        /// so no writer changes and nothing can disagree with the parent.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> ThroughParent = new Dictionary<string, string>
        {
            ["item_criterion"] = OfItem("item_criterion"),
            ["attempt"] = OfItem("attempt"),
            ["artifact"] = OfItem("artifact"),
            ["claim"] = OfItem("claim"),
            ["project_config"] = OfItem("project_config"),
            ["run_config"] = OfItem("run_config"),
            ["report"] =
                $"EXISTS (SELECT 1 FROM attempt a JOIN item p ON p.id = a.item_id WHERE a.id = report.attempt_id AND p.workspace_id = {Tenant})",
            ["doc_block"] = $"EXISTS (SELECT 1 FROM doc d WHERE d.id = doc_block.doc_id AND d.workspace_id = {Tenant})",
            ["doc_block_revision"] =
                $"EXISTS (SELECT 1 FROM doc_block b JOIN doc d ON d.id = b.doc_id WHERE b.id = doc_block_revision.block_id AND d.workspace_id = {Tenant})",
        };

        /// <summary>The server's own tables, which the role never needs: only the owner reads or writes them.</summary>
        public static readonly IReadOnlyList<string> OwnerOnly =
        [
            "\"__EFMigrationsHistory\"", "data_protection_key", "oauth_key",
            "openiddict_application", "openiddict_authorization", "openiddict_scope", "openiddict_token",
        ];

        /// <summary>
        /// The functions behind the subtree (<c>item_subtree_idx</c>), ref (<c>item_ref_lower_idx</c>) and search
        /// (<c>item_search_idx</c>, <c>note_search_idx</c>) indexes. Under a policy, Postgres uses a function as an index condition only if it
        /// is LEAKPROOF, and these are not by default: every subtree read became a scan of the workspace (doc 15 §7).
        /// Superuser-only, per database, and not carried by pg_dump, so <c>SchemaOwner</c> re-asserts them at startup.
        /// </summary>
        public static readonly IReadOnlyList<string> Leakproof =
            ["arraycontains(anyarray, anyarray)", "lower(text)", "ts_match_vq(tsvector, tsquery)", "to_tsvector(regconfig, text)"];

        /// <summary>A view runs as its owner unless told otherwise, and the owner is not confined.</summary>
        public static readonly IReadOnlyList<string> Views = ["item_rollup", "open_thread"];

        private const string Role = WorkStateConnections.AppRole;

        private static string OfItem(string table)
            => $"EXISTS (SELECT 1 FROM item p WHERE p.id = {table}.item_id AND p.workspace_id = {Tenant})";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A role is the cluster's, not the database's: every database of the cluster that migrates shares it,
            // and two migrating at once race to create it.
            migrationBuilder.Sql(
                $"""
                 DO $$
                 BEGIN
                   CREATE ROLE {Role} NOLOGIN;
                 EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL;
                 END $$;

                 DO $$
                 BEGIN
                   IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
                     EXECUTE format('GRANT {Role} TO %I', current_user);
                   END IF;
                 END $$;

                 GRANT USAGE ON SCHEMA public TO {Role};
                 GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {Role};
                 GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {Role};
                 REVOKE ALL ON {string.Join(", ", OwnerOnly)} FROM {Role};
                 ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {Role};
                 ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {Role};
                 """);

            foreach (var (table, column) in Direct)
            {
                Enable(migrationBuilder, table, $"{column} = {Tenant}");
            }

            foreach (var (table, parent) in ThroughParent)
            {
                Enable(migrationBuilder, table, parent);
            }

            foreach (var view in Views)
            {
                migrationBuilder.Sql($"ALTER VIEW {view} SET (security_invoker = true);");
            }

            foreach (var function in Leakproof)
            {
                migrationBuilder.Sql($"ALTER FUNCTION {function} LEAKPROOF;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var function in Leakproof)
            {
                migrationBuilder.Sql($"ALTER FUNCTION {function} NOT LEAKPROOF;");
            }

            foreach (var view in Views)
            {
                migrationBuilder.Sql($"ALTER VIEW {view} RESET (security_invoker);");
            }

            foreach (var table in Direct.Keys.Concat(ThroughParent.Keys))
            {
                migrationBuilder.Sql(
                    $"""
                     DROP POLICY IF EXISTS {Policy} ON {table};
                     ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
                     ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
                     """);
            }

            // The role goes only with the last database that granted it anything.
            migrationBuilder.Sql(
                $"""
                 ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM {Role};
                 ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM {Role};
                 REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {Role};
                 REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {Role};
                 REVOKE USAGE ON SCHEMA public FROM {Role};

                 DO $$
                 BEGIN
                   DROP ROLE {Role};
                 EXCEPTION WHEN dependent_objects_still_exist OR object_in_use THEN NULL;
                 END $$;
                 """);
        }

        private static void Enable(MigrationBuilder migrationBuilder, string table, string predicate)
            => migrationBuilder.Sql(
                $"""
                 ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                 ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                 CREATE POLICY {Policy} ON {table} USING ({predicate}) WITH CHECK ({predicate});
                 """);
    }
}

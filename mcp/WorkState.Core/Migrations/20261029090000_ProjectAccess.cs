using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Data;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 §15b. A project's rows reach its members only, enforced as TenantRls enforces the workspace: one
    /// restrictive policy per table whose rows belong to a project, ANDed with the tenant policy, so a query that
    /// forgets to filter on membership leaks nothing. The tenant helper sets <see cref="WorkStateConnections.UserSetting"/>
    /// for the request's user; unset or <c>''</c> — a credential with no user — hides no project of the tenant.
    /// </summary>
    public partial class ProjectAccess : Migration
    {
        public const string Policy = "project_access";

        public const string HiddenFunction = "workstate_hidden_projects";

        /// <summary>
        /// The caller's hidden projects, once per statement: a scalar subquery is an InitPlan, so every row pays an
        /// array comparison and nothing more. The cast makes <c>= ANY (…)</c> read an array, not a subquery's rows.
        /// </summary>
        public const string Hidden = $"(SELECT {HiddenFunction}())::uuid[]";

        /// <summary>
        /// Tables whose rows belong to a project, and the policy that keeps them to its members. An item is its root's:
        /// the project at the top of its tree, or itself. A row that hangs off an item is visible when that item is —
        /// the subquery reads <c>item</c> under its own policies — and a nullable reference left null is a workspace
        /// row, visible to the tenant. The tables that reach an item through a parent (<see cref="TenantRls.ThroughParent"/>)
        /// already read it in their tenant policy, so they follow it with no policy of their own.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Tables = new Dictionary<string, string>
        {
            ["item"] = $"NOT (coalesce(item.ancestors[1], item.id) = ANY ({Hidden}))",
            ["project_member"] = $"NOT (project_member.project_item_id = ANY ({Hidden}))",
            ["item_link"] = $"{Of("item_link.from_item_id")} AND {Of("item_link.to_item_id")}",
            ["doc"] = OfOptional("doc.item_id"),
            ["event"] = OfOptional("event.item_id"),
            ["assumption"] = OfOptional("assumption.scope_item_id"),
            ["constraint_rule"] = OfOptional("constraint_rule.scope_item_id"),
            ["note"] = $"{OfOptional("note.to_item_id")} AND {OfOptional("note.from_item_id")}",
            ["verification"] = Of("verification.item_id"),
            ["project_repo"] = Of("project_repo.project_item_id"),
            ["agent_token"] = OfOptional("agent_token.scope_item_id"),
            ["import_record"] = Of("import_record.project_item_id"),
        };

        private static string Of(string column) => $"EXISTS (SELECT 1 FROM item p WHERE p.id = {column})";

        private static string OfOptional(string column) => $"({column} IS NULL OR {Of(column)})";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The projects of the tenant that have members, none of them the caller: a project no one has joined
            // yet, and a root that is no project (a parentless feature), have no members and so are hidden from no
            // one. With no user, nothing is hidden. As the owner, so the policies need no grant on project_member and
            // cannot recurse through its own policy; STABLE, so it reads the statement's snapshot — the same one the
            // rows it filters come from, so a project and its first member are never seen apart.
            migrationBuilder.Sql(
                $"""
                 CREATE FUNCTION {HiddenFunction}() RETURNS uuid[]
                 LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
                   SELECT coalesce(array_agg(DISTINCT m.project_item_id), ARRAY[]::uuid[])
                   FROM   project_member m
                   WHERE  m.workspace_id = NULLIF(current_setting('{WorkStateConnections.WorkspaceSetting}', true), '')::uuid
                     AND  NULLIF(current_setting('{WorkStateConnections.UserSetting}', true), '') IS NOT NULL
                     AND  NOT EXISTS (SELECT 1 FROM project_member me
                                      WHERE  me.project_item_id = m.project_item_id
                                        AND  me.user_id = NULLIF(current_setting('{WorkStateConnections.UserSetting}', true), '')::uuid);
                 $fn$;
                 """);

            foreach (var (table, predicate) in Tables)
            {
                migrationBuilder.Sql(
                    $"CREATE POLICY {Policy} ON {table} AS RESTRICTIVE USING ({predicate}) WITH CHECK ({predicate});");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables.Keys)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS {Policy} ON {table};");
            }

            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {HiddenFunction}();");
        }
    }
}

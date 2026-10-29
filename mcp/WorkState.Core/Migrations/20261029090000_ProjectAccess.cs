using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Data;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 §15b. A project's rows reach its members only, enforced as TenantRls enforces the workspace: one
    /// restrictive policy per table whose rows belong to a project, ANDed with the tenant policy, so a query that
    /// forgets to filter on membership leaks nothing. The tenant helper sets <see cref="WorkStateConnections.UserSetting"/>
    /// for the request's user, and <see cref="WorkStateConnections.ProjectsSetting"/> to the roots that user may see;
    /// no user — a credential that has none — hides no project of the tenant.
    /// </summary>
    public partial class ProjectAccess : Migration
    {
        public const string Policy = "project_access";

        public const string HiddenFunction = "workstate_hidden_projects";

        public const string VisibleFunction = "workstate_visible_projects";

        /// <summary>No user in the scope: every project. A scalar subquery, so a plan reads it once, as an InitPlan.</summary>
        public const string NoUser = $"(SELECT NULLIF(current_setting('{WorkStateConnections.UserSetting}', true), '') IS NULL)";

        /// <summary>
        /// The roots the user could see when the transaction began, as the tenant helper set them. Parsed inside the
        /// InitPlan, once; the outer cast changes nothing but makes <c>= ANY (…)</c> read an array, not a subquery's rows.
        /// </summary>
        public const string Visible = $"(SELECT NULLIF(current_setting('{WorkStateConnections.ProjectsSetting}', true), '')::uuid[])::uuid[]";

        /// <summary>The projects hidden from the user now; the cast makes <c>= ANY (…)</c> read an array, not rows.</summary>
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
            ["item"] = OfRoot("coalesce(item.ancestors[1], item.id)"),
            ["project_member"] = OfRoot("project_member.project_item_id"),
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

        /// <summary>
        /// A root the user may see. The arms are tried in order and an InitPlan runs only when its arm is reached: a row
        /// of a root they could see when the transaction began pays an array comparison; any other root — one hidden from
        /// them, or one created since — asks <see cref="HiddenFunction"/>, once per statement.
        /// </summary>
        private static string OfRoot(string root) => $"({NoUser} OR {root} = ANY ({Visible}) OR NOT ({root} = ANY ({Hidden})))";

        /// <summary>With no user nothing is hidden, so the row's item is not looked up.</summary>
        private static string Of(string column) => $"({NoUser} OR EXISTS (SELECT 1 FROM item p WHERE p.id = {column}))";

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

            // The roots of the workspace the user may see — a project they are a member of, one no one has joined, a
            // root that is no project — as the text of a uuid[] for ProjectsSetting. The tenant helper calls it in the
            // statement that sets the other two, with their values as arguments.
            migrationBuilder.Sql(
                $"""
                 CREATE FUNCTION {VisibleFunction}(p_workspace uuid, p_user uuid) RETURNS text
                 LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
                   SELECT CASE WHEN p_workspace IS NULL OR p_user IS NULL THEN '' ELSE (
                     SELECT coalesce(array_agg(r.id), ARRAY[]::uuid[])::text
                     FROM   item r
                     WHERE  r.workspace_id = p_workspace AND r.parent_id IS NULL
                       AND  (EXISTS (SELECT 1 FROM project_member m WHERE m.project_item_id = r.id AND m.user_id = p_user)
                             OR NOT EXISTS (SELECT 1 FROM project_member m WHERE m.project_item_id = r.id))) END;
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

            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {VisibleFunction}(uuid, uuid);");
            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {HiddenFunction}();");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Data;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 §15b. ProjectAccess's policies with a fast path that costs a member nothing per row. <c>workspace.access_seq</c>
    /// moves, by trigger, on every change to who may see which project — a member added or removed, a root created,
    /// moved or deleted. The tenant helper records it with the projects hidden from the user, in the statement that
    /// sets the tenant; while a statement still reads the same seq, that list is the truth, and when it does not, the
    /// policy asks the live <see cref="ProjectAccess.HiddenFunction"/>. A change another transaction commits is seen by
    /// the caller's next statement.
    /// </summary>
    public partial class ProjectAccessSeq : Migration
    {
        public const string AccessFunction = "workstate_project_access";

        public const string BumpFunction = "workstate_bump_access_seq";

        /// <summary>The triggers that move the seq, and what each catches.</summary>
        public static readonly IReadOnlyDictionary<string, string> Triggers = new Dictionary<string, string>
        {
            ["project_member_access_seq_trg"] = "AFTER INSERT OR UPDATE OR DELETE ON project_member FOR EACH ROW",
            ["item_root_insert_access_seq_trg"] = "AFTER INSERT ON item FOR EACH ROW WHEN (NEW.parent_id IS NULL)",
            ["item_root_move_access_seq_trg"] =
                "AFTER UPDATE OF parent_id ON item FOR EACH ROW WHEN ((OLD.parent_id IS NULL) <> (NEW.parent_id IS NULL))",
            ["item_root_delete_access_seq_trg"] = "AFTER DELETE ON item FOR EACH ROW WHEN (OLD.parent_id IS NULL)",
        };

        /// <summary>No user in the scope: every project. A scalar subquery, so a plan reads it once, as an InitPlan.</summary>
        public const string NoUser = ProjectAccess.NoUser;

        /// <summary>
        /// Nothing about who sees what has changed since the transaction began: the workspace row this statement reads
        /// carries the seq the tenant helper recorded. Null when none was recorded, which takes the live path.
        /// </summary>
        public const string Unchanged =
            $"(SELECT w.access_seq = NULLIF(current_setting('{WorkStateConnections.AccessSeqSetting}', true), '')::bigint "
            + $"FROM workspace w WHERE w.id = {TenantRls.Tenant})";

        /// <summary>The projects hidden from the user when the transaction began, parsed once inside its InitPlan.</summary>
        public const string HiddenAtStart =
            $"(SELECT NULLIF(current_setting('{WorkStateConnections.HiddenSetting}', true), '')::uuid[])::uuid[]";

        /// <summary>Nothing was hidden from the user when the transaction began.</summary>
        public const string NothingHidden = $"(SELECT current_setting('{WorkStateConnections.HiddenSetting}', true) = '{{}}')";

        /// <summary>Tables and policies, as ProjectAccess listed them; only the predicates change.</summary>
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
        /// A root is visible with no user; else, unchanged since the transaction began, when it was not hidden then; else
        /// when it is not hidden now. The live function runs only on the last branch.
        /// </summary>
        private static string OfRoot(string root)
            => $"({NoUser} OR CASE WHEN {Unchanged} THEN NOT ({root} = ANY ({HiddenAtStart})) "
               + $"ELSE NOT ({root} = ANY ({ProjectAccess.Hidden})) END)";

        /// <summary>
        /// A row that hangs off an item. When nothing was hidden and nothing has changed, every item is visible, so the
        /// row's item is not looked up; otherwise it is, under <c>item</c>'s policies.
        /// </summary>
        private static string Of(string column)
            => $"({NoUser} OR ({Unchanged} AND {NothingHidden}) OR EXISTS (SELECT 1 FROM item p WHERE p.id = {column}))";

        private static string OfOptional(string column) => $"({column} IS NULL OR {Of(column)})";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The database's own: only the triggers below write it and only the policies read it, so the EF model,
            // which seeds databases of every earlier migration in the tests, leaves it out.
            migrationBuilder.Sql("ALTER TABLE workspace ADD COLUMN access_seq bigint NOT NULL DEFAULT 0;");

            // As the owner, so the bump lands whatever the writer's own policies let it see. The workspace row it
            // updates is the lock that orders these writes within a workspace.
            migrationBuilder.Sql(
                $"""
                 CREATE FUNCTION {BumpFunction}() RETURNS trigger
                 LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
                 BEGIN
                   IF TG_OP <> 'DELETE' THEN
                     UPDATE workspace SET access_seq = access_seq + 1 WHERE id = NEW.workspace_id;
                   END IF;
                   IF TG_OP = 'DELETE' OR OLD.workspace_id IS DISTINCT FROM NEW.workspace_id THEN
                     UPDATE workspace SET access_seq = access_seq + 1 WHERE id = OLD.workspace_id;
                   END IF;
                   RETURN NULL;
                 END
                 $fn$;
                 """);

            foreach (var (trigger, when) in Triggers)
            {
                migrationBuilder.Sql($"CREATE TRIGGER {trigger} {when} EXECUTE FUNCTION {BumpFunction}();");
            }

            // The seq and the projects hidden from the user, read in one statement and so from one snapshot: the list
            // is exactly what that seq stands for. Called by the tenant helper with the values it is setting.
            migrationBuilder.Sql(
                $"""
                 CREATE FUNCTION {AccessFunction}(p_workspace uuid, p_user uuid, OUT seq text, OUT hidden text)
                 LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
                   SELECT w.access_seq::text,
                          coalesce((SELECT array_agg(DISTINCT m.project_item_id)
                                    FROM   project_member m
                                    WHERE  m.workspace_id = p_workspace
                                      AND  NOT EXISTS (SELECT 1 FROM project_member me
                                                       WHERE  me.project_item_id = m.project_item_id AND me.user_id = p_user)),
                                   ARRAY[]::uuid[])::text
                   FROM   workspace w
                   WHERE  w.id = p_workspace;
                 $fn$;
                 """);

            foreach (var (table, predicate) in Tables)
            {
                migrationBuilder.Sql(
                    $"""
                     DROP POLICY {ProjectAccess.Policy} ON {table};
                     CREATE POLICY {ProjectAccess.Policy} ON {table} AS RESTRICTIVE USING ({predicate}) WITH CHECK ({predicate});
                     """);
            }

            migrationBuilder.Sql($"DROP FUNCTION {ProjectAccess.VisibleFunction}(uuid, uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ProjectAccess.VisibleFunctionSql);

            foreach (var (table, predicate) in ProjectAccess.Tables)
            {
                migrationBuilder.Sql(
                    $"""
                     DROP POLICY IF EXISTS {ProjectAccess.Policy} ON {table};
                     CREATE POLICY {ProjectAccess.Policy} ON {table} AS RESTRICTIVE USING ({predicate}) WITH CHECK ({predicate});
                     """);
            }

            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {AccessFunction}(uuid, uuid);");

            foreach (var trigger in Triggers.Keys)
            {
                var table = trigger.StartsWith("project_member", StringComparison.Ordinal) ? "project_member" : "item";
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {trigger} ON {table};");
            }

            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {BumpFunction}();");

            migrationBuilder.Sql("ALTER TABLE workspace DROP COLUMN access_seq;");
        }
    }
}

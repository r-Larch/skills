using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 "Ref scoping". A project or a run is its own ref scope (<c>ref_scope_id = id</c>), so
    /// <c>item_ref_unique</c> never compared two of them: two runs <c>1</c> under one project, or two projects
    /// <c>x</c> in one workspace, both inserted, and <c>gaia/1</c> named two runs. This makes a self-scoping
    /// row's ref unique among its siblings, case-insensitively, after renaming the duplicates already stored.
    /// </summary>
    public partial class ScopeRefUnique : Migration
    {
        public const string Index = "item_sibling_ref_unique";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The oldest of each group keeps its ref; every other becomes <ref>-2, <ref>-3, … — the first
            // free among its siblings and in its parent's scope, which is where the writer probes too.
            // Journalled as `updated`, the kind work_update writes for an edited item.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                  r     record;
                  n     int;
                  fresh text;
                BEGIN
                  FOR r IN
                    SELECT d.id, d.workspace_id, d.parent_id, d.ref, d.kind, p.ref_scope_id AS parent_scope, p.ref AS parent_ref
                    FROM  (SELECT i.*, row_number() OVER (PARTITION BY i.workspace_id, i.parent_id, lower(i.ref)
                                                          ORDER BY i.seq, i.id) AS nth
                           FROM   item i
                           WHERE  i.ref_scope_id = i.id) d
                    LEFT  JOIN item p ON p.id = d.parent_id
                    WHERE d.nth > 1
                    ORDER BY d.workspace_id, d.parent_id, lower(d.ref), d.nth
                  LOOP
                    n := 2;
                    LOOP
                      fresh := r.ref || '-' || n;
                      EXIT WHEN NOT EXISTS (
                        SELECT 1 FROM item o
                        WHERE  o.workspace_id = r.workspace_id
                          AND  lower(o.ref) = lower(fresh)
                          AND  ((o.ref_scope_id = o.id AND o.parent_id IS NOT DISTINCT FROM r.parent_id)
                                OR o.ref_scope_id IS NOT DISTINCT FROM r.parent_scope));
                      n := n + 1;
                    END LOOP;

                    UPDATE item SET ref = fresh, revision = revision + 1 WHERE id = r.id;

                    INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary)
                    VALUES (r.workspace_id, r.id, 'migration', 'system', 'updated',
                            jsonb_build_object('fields', jsonb_build_array('ref'), 'from', r.ref, 'to', fresh,
                                               'migration', 'ScopeRefUnique'),
                            coalesce(r.parent_ref || '/', '') || r.ref || ' (' || r.kind || ') renamed ' || fresh
                              || ' — ' || coalesce(r.parent_ref, 'the workspace') || ' already had a ' || r.kind || ' ' || r.ref);
                  END LOOP;
                END $$;
                """);

            // `ref_scope_id = id` is exactly the self-scoping kinds (item_scope_of), so the predicate needs
            // neither the kinds' keys spelled out nor a column that copies item_kind.scopes_refs.
            // NULLS NOT DISTINCT, so top-level projects collide with each other.
            migrationBuilder.Sql(
                $"""
                 CREATE UNIQUE INDEX {Index} ON item (workspace_id, parent_id, lower(ref))
                   NULLS NOT DISTINCT WHERE ref_scope_id = id;
                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The renamed refs stay renamed: putting a duplicate back is not a repair.
            migrationBuilder.Sql($"DROP INDEX IF EXISTS {Index};");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class Views : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // doc 02 §13. One query layer, two presentations: the MCP tools and the board read these.
            migrationBuilder.Sql(
                """
                CREATE VIEW item_rollup AS
                SELECT p.id,
                       count(*) FILTER (WHERE c.status IN ('open','ready'))  AS open_count,
                       count(*) FILTER (WHERE c.status = 'in_progress')      AS doing_count,
                       count(*) FILTER (WHERE c.status = 'blocked')          AS blocked_count,
                       count(*) FILTER (WHERE c.status = 'done')             AS done_count
                FROM   item p
                LEFT JOIN item c ON c.ancestors @> ARRAY[p.id]
                GROUP  BY p.id;
                """);

            migrationBuilder.Sql(
                """
                CREATE VIEW open_thread AS
                  SELECT 'assumption' AS t, a.ref, a.statement AS text, a.scope_item_id AS scope,
                         a.workspace_id
                    FROM assumption a WHERE a.state = 'open'
                  UNION ALL
                  SELECT 'finding', i.ref, i.title, i.parent_id, i.workspace_id
                    FROM item i
                   WHERE i.kind = 'finding' AND i.status NOT IN ('done','cancelled','superseded')
                  UNION ALL
                  SELECT 'followup', i.ref, i.title, i.parent_id, i.workspace_id
                    FROM item i
                   WHERE i.kind = 'followup' AND i.status NOT IN ('done','cancelled','superseded')
                  UNION ALL
                  SELECT 'note', coalesce(fi.ref, '—'), n.body, n.to_item_id, n.workspace_id
                    FROM note n
                    LEFT JOIN item fi ON fi.id = n.from_item_id
                   WHERE n.consumed_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS open_thread;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS item_rollup;");
        }
    }
}

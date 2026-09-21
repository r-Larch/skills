using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class RollupReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // doc 02 §13. `review` is unclosed work in flight and had no bucket, so it fell out of
            // every "X of Y" the board prints. `cancelled` and `superseded` stay out on purpose:
            // they are closed and were not done.
            migrationBuilder.Sql("DROP VIEW IF EXISTS item_rollup;");
            migrationBuilder.Sql(
                """
                CREATE VIEW item_rollup AS
                SELECT p.id,
                       count(*) FILTER (WHERE c.status IN ('open','ready'))  AS open_count,
                       count(*) FILTER (WHERE c.status = 'in_progress')      AS doing_count,
                       count(*) FILTER (WHERE c.status = 'blocked')          AS blocked_count,
                       count(*) FILTER (WHERE c.status = 'review')           AS review_count,
                       count(*) FILTER (WHERE c.status = 'done')             AS done_count
                FROM   item p
                LEFT JOIN item c ON c.ancestors @> ARRAY[p.id]
                GROUP  BY p.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS item_rollup;");
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
        }
    }
}

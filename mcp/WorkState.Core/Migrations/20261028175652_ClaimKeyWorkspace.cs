using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class ClaimKeyWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every key now locks within its workspace, a path too (TreeLockAsync). A live claim taken
            // before that holds its path server-wide under a NULL workspace, which a new claim on the same
            // path, keyed with one, would neither see nor collide with. Released claims keep their NULL.
            migrationBuilder.Sql(
                """
                UPDATE claim c
                   SET key_workspace_id = i.workspace_id
                  FROM item i
                 WHERE c.item_id = i.id
                   AND c.released_at IS NULL
                   AND c.key_workspace_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: which keys were paths is not recorded. An older build does not see a path
            // claimed after Up as held until that claim is released.
        }
    }
}

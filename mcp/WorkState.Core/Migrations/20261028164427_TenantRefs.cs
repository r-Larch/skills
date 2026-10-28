using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class TenantRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A ref is unique per workspace. A NULL ref_scope_id (a workspace-wide assumption, a
            // feature with no parent) otherwise collided with the same ref in every other workspace.
            migrationBuilder.Sql(
                """
                ALTER TABLE item
                  DROP CONSTRAINT item_ref_unique,
                  ADD CONSTRAINT item_ref_unique UNIQUE NULLS NOT DISTINCT (workspace_id, ref_scope_id, ref);

                ALTER TABLE assumption
                  DROP CONSTRAINT assumption_ref_unique,
                  ADD CONSTRAINT assumption_ref_unique UNIQUE NULLS NOT DISTINCT (workspace_id, ref_scope_id, ref);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails, and changes nothing, once two workspaces hold the same NULL-scope ref.
            migrationBuilder.Sql(
                """
                ALTER TABLE assumption
                  DROP CONSTRAINT assumption_ref_unique,
                  ADD CONSTRAINT assumption_ref_unique UNIQUE NULLS NOT DISTINCT (ref_scope_id, ref);

                ALTER TABLE item
                  DROP CONSTRAINT item_ref_unique,
                  ADD CONSTRAINT item_ref_unique UNIQUE NULLS NOT DISTINCT (ref_scope_id, ref);
                """);
        }
    }
}

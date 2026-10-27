using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class RefLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // doc 15. Ref resolution and the RefShared flag compare lower(ref), which no index served:
            // every resolve was a sequential scan of item. An expression index, so not in the EF model.
            migrationBuilder.Sql("CREATE INDEX item_ref_lower_idx ON item (workspace_id, lower(ref));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS item_ref_lower_idx;");
        }
    }
}

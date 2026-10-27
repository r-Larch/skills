using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class NoteSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // doc 15. work_search matches note bodies; without this every search ran to_tsvector over
            // every note. An expression index, so not in the EF model; WorkStateQueries.SearchAsync must
            // match on exactly this expression or the planner cannot use it.
            migrationBuilder.Sql("CREATE INDEX note_search_idx ON note USING gin (to_tsvector('english', body));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS note_search_idx;");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ref",
                table: "constraint_rule",
                type: "text",
                nullable: false,
                defaultValue: "");

            // The corpus arrived through the importer, which had no ref to write — 37 rules for one
            // run alone. A nullable column left empty for every imported rule is the same defect
            // wearing a column, so they are numbered here, before the unique index exists to refuse
            // the duplicate empty strings AddColumn just wrote.
            //
            // Numbered per WORKSPACE, not per run: one BINDING block renders the workspace-wide
            // rules together with the ones scoped to the item's ancestors, and two C1s in one block
            // is a ref nobody can cite. created_at then id, so a re-run gives the same answer.
            migrationBuilder.Sql(
                """
                WITH numbered AS (
                  SELECT id,
                         'C' || row_number() OVER (PARTITION BY workspace_id
                                                   ORDER BY created_at, id) AS ref
                  FROM   constraint_rule
                )
                UPDATE constraint_rule c
                   SET ref = n.ref
                  FROM numbered n
                 WHERE n.id = c.id;
                """);

            migrationBuilder.CreateIndex(
                name: "constraint_ref_unique",
                table: "constraint_rule",
                columns: new[] { "workspace_id", "ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "constraint_ref_unique",
                table: "constraint_rule");

            migrationBuilder.DropColumn(
                name: "ref",
                table: "constraint_rule");
        }
    }
}

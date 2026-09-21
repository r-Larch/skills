using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Domain;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class NullableEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "command",
                table: "verification",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<ReversalCost>(
                name: "reversal",
                table: "assumption",
                type: "reversal_cost",
                nullable: true,
                oldClrType: typeof(ReversalCost),
                oldType: "reversal_cost");

            migrationBuilder.AddColumn<string>(
                name: "evidence_note",
                table: "assumption",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "evidence_note",
                table: "assumption");

            migrationBuilder.AlterColumn<string>(
                name: "command",
                table: "verification",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<ReversalCost>(
                name: "reversal",
                table: "assumption",
                type: "reversal_cost",
                nullable: false,
                defaultValue: ReversalCost.Trivial,
                oldClrType: typeof(ReversalCost),
                oldType: "reversal_cost",
                oldNullable: true);
        }
    }
}

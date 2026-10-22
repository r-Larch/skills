using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class ClaimPerTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "claim_one_writer_per_tree",
                table: "claim");

            migrationBuilder.AddColumn<Guid>(
                name: "key_workspace_id",
                table: "claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tree",
                table: "claim",
                type: "text",
                nullable: true);

            // Keys are left as they are. A key that came from the ref-scope fallback (no run key, no
            // repo root) is a name, so it is narrowed to its workspace, as a new claim on it would
            // be; every other key stays server-wide. Narrowing cannot create a duplicate.
            migrationBuilder.Sql(
                """
                UPDATE claim c
                   SET key_workspace_id = i.workspace_id
                  FROM item i
                  LEFT JOIN item s ON s.id = i.ref_scope_id
                 WHERE c.item_id = i.id
                   AND c.serialization_key = coalesce(s.ref, i.ref)
                   AND NOT EXISTS (SELECT 1 FROM run_config rc
                                    WHERE rc.item_id = ANY(i.ancestors || i.id) AND rc.serialization_key <> '')
                   AND NOT EXISTS (SELECT 1 FROM project_config pc
                                    WHERE pc.item_id = ANY(i.ancestors || i.id) AND pc.repo_root <> '');
                """);

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                columns: new[] { "serialization_key", "key_workspace_id" },
                unique: true,
                filter: "released_at IS NULL AND writes")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "claim_one_writer_per_tree",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "key_workspace_id",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "tree",
                table: "claim");

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                column: "serialization_key",
                unique: true,
                filter: "released_at IS NULL AND writes");
        }
    }
}

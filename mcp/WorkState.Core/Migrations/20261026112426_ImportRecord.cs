using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class ImportRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_path = table.Column<string>(type: "text", nullable: false),
                    source_anchor = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    kind = table.Column<string>(type: "text", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assumption_id = table.Column<Guid>(type: "uuid", nullable: true),
                    constraint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    why = table.Column<string>(type: "text", nullable: false),
                    imported_status = table.Column<string>(type: "text", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_record", x => x.id);
                    table.CheckConstraint("import_record_kind", "kind IN ('run','phase','task','followup','note','assumption','rule')");
                    table.CheckConstraint("import_record_one_target", "num_nonnulls(item_id, note_id, assumption_id, constraint_id) = 1");
                    table.ForeignKey(
                        name: "fk_import_record_agent_session_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_import_record_assumption_assumption_id",
                        column: x => x.assumption_id,
                        principalTable: "assumption",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_record_constraint_rule_constraint_id",
                        column: x => x.constraint_id,
                        principalTable: "constraint_rule",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_record_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_record_item_project_item_id",
                        column: x => x.project_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_record_note_note_id",
                        column: x => x.note_id,
                        principalTable: "note",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_record_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "import_record_item_idx",
                table: "import_record",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "import_record_source_unique",
                table: "import_record",
                columns: new[] { "project_item_id", "source_path", "source_anchor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_record_assumption_id",
                table: "import_record",
                column: "assumption_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_record_constraint_id",
                table: "import_record",
                column: "constraint_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_record_note_id",
                table: "import_record",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_record_session_id",
                table: "import_record",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_record_workspace_id",
                table: "import_record",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_record");
        }
    }
}

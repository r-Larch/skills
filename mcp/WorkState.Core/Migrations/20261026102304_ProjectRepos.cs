using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class ProjectRepos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "repo_remote",
                table: "agent_session",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repo_root",
                table: "agent_session",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_repo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_repo", x => x.id);
                    table.CheckConstraint("project_repo_kind", "kind IN ('remote','root')");
                    table.ForeignKey(
                        name: "fk_project_repo_item_project_item_id",
                        column: x => x.project_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_repo_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "project_repo_key_unique",
                table: "project_repo",
                columns: new[] { "workspace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "project_repo_project_idx",
                table: "project_repo",
                column: "project_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_repo");

            migrationBuilder.DropColumn(
                name: "repo_remote",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "repo_root",
                table: "agent_session");
        }
    }
}

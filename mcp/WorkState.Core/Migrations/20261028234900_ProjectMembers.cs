using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Data;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 §12b. Who may work in a project: <c>project_member</c>, a tenant table under the same policy as every
    /// other (doc 02 §15), and every project that exists already gets every member of its workspace — the
    /// tenant-wide project it has been until now. A project created from here on is its creator's alone.
    /// </summary>
    public partial class ProjectMembers : Migration
    {
        /// <summary>The table's workspace column, for the policy; <see cref="TenantTables"/> lists it with TenantRls's.</summary>
        public static readonly IReadOnlyDictionary<string, string> Direct = new Dictionary<string, string>
        {
            ["project_member"] = "workspace_id",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_member",
                columns: table => new
                {
                    project_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_member", x => new { x.project_item_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_project_member_app_user_added_by",
                        column: x => x.added_by,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_project_member_item_project_item_id",
                        column: x => x.project_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_member_tenant_membership_user_id_workspace_id",
                        columns: x => new { x.user_id, x.workspace_id },
                        principalTable: "tenant_membership",
                        principalColumns: new[] { "user_id", "workspace_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_member_added_by",
                table: "project_member",
                column: "added_by");

            migrationBuilder.CreateIndex(
                name: "ix_project_member_user_id_workspace_id",
                table: "project_member",
                columns: new[] { "user_id", "workspace_id" });

            // Default privileges grant a new table to the role only when the role that set them creates it.
            migrationBuilder.Sql($"GRANT SELECT, INSERT, UPDATE, DELETE ON project_member TO {WorkStateConnections.AppRole};");

            foreach (var (table, column) in Direct)
            {
                var predicate = $"{column} = {TenantRls.Tenant}";
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                     ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                     CREATE POLICY {TenantRls.Policy} ON {table} USING ({predicate}) WITH CHECK ({predicate});
                     """);
            }

            migrationBuilder.Sql(
                """
                INSERT INTO project_member (project_item_id, user_id, workspace_id)
                SELECT i.id, m.user_id, m.workspace_id
                FROM   item i
                JOIN   tenant_membership m ON m.workspace_id = i.workspace_id
                WHERE  i.kind = 'project';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_member");
        }
    }
}

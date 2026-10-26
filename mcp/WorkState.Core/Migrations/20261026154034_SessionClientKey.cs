using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionClientKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_key",
                table: "agent_session",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "agent_session_client_key_unique",
                table: "agent_session",
                columns: new[] { "workspace_id", "client_key" },
                unique: true,
                filter: "client_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "agent_session_client_key_unique",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "client_key",
                table: "agent_session");
        }
    }
}

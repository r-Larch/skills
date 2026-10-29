using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class AgentSessionUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A name is unique per (workspace, user). Every row here is a static token's or a header's, with no
            // user, and NULLS NOT DISTINCT keeps those sharing one name per workspace exactly as before.
            migrationBuilder.DropIndex(
                name: "agent_session_name_unique",
                table: "agent_session");

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "agent_session",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "agent_session_name_unique",
                table: "agent_session",
                columns: new[] { "workspace_id", "user_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refused while two users' sessions share a name in one workspace: release one of them first.
            migrationBuilder.DropIndex(
                name: "agent_session_name_unique",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "agent_session");

            migrationBuilder.CreateIndex(
                name: "agent_session_name_unique",
                table: "agent_session",
                columns: new[] { "workspace_id", "name" },
                unique: true);
        }
    }
}

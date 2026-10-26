using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Domain;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class AgentSessionTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ActorRole>(
                name: "granted_role",
                table: "agent_session",
                type: "actor_role",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "minted_by_session_id",
                table: "agent_session",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                table: "agent_session",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "token_expires_at",
                table: "agent_session",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "token_hash",
                table: "agent_session",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "agent_session_token_hash_unique",
                table: "agent_session",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_minted_by_session_id",
                table: "agent_session",
                column: "minted_by_session_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_session_agent_session_minted_by_session_id",
                table: "agent_session",
                column: "minted_by_session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_session_agent_session_minted_by_session_id",
                table: "agent_session");

            migrationBuilder.DropIndex(
                name: "agent_session_token_hash_unique",
                table: "agent_session");

            migrationBuilder.DropIndex(
                name: "ix_agent_session_minted_by_session_id",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "granted_role",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "minted_by_session_id",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "token_expires_at",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "token_hash",
                table: "agent_session");
        }
    }
}

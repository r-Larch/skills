using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class OAuthMintedSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "minted_by_authorization_id",
                table: "agent_session",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "minted_by_subject",
                table: "agent_session",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "minted_by_authorization_id",
                table: "agent_session");

            migrationBuilder.DropColumn(
                name: "minted_by_subject",
                table: "agent_session");
        }
    }
}

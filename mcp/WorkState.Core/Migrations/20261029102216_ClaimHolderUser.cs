using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 07 §5. A claim records the user who took it, because users share role names: without it a claim with no
    /// session belonged to every user whose agent had the same name. A live claim taken by a session is that session's
    /// user's; every other claim keeps NULL, which is the caller's own only for a credential that has no user either.
    /// </summary>
    public partial class ClaimHolderUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "holder_user_id",
                table: "claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE claim c
                   SET holder_user_id = s.user_id
                  FROM agent_session s
                 WHERE s.id = c.session_id
                   AND c.released_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "holder_user_id",
                table: "claim");
        }
    }
}

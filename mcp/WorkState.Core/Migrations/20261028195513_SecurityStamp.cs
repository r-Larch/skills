using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class SecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A volatile default, so every existing user gets a stamp of their own. Cookies issued before it carry
            // none and sign in again, as cookies did before the user id and the tenant were on them.
            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                table: "app_user",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "security_stamp",
                table: "app_user");
        }
    }
}

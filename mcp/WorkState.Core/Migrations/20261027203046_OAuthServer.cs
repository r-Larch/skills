using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class OAuthServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_key",
                columns: table => new
                {
                    purpose = table.Column<string>(type: "text", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    protected_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_key", x => x.purpose);
                    table.CheckConstraint("oauth_key_purpose_known", "purpose IN ('signing', 'encryption')");
                });

            migrationBuilder.CreateTable(
                name: "openiddict_application",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_application", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_scope",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_scope", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "openiddict_authorization",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_authorization", x => x.id);
                    table.ForeignKey(
                        name: "fk_openiddict_authorization_openiddict_application_application",
                        column: x => x.application_id,
                        principalTable: "openiddict_application",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "openiddict_token",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    authorization_id = table.Column<string>(type: "text", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_openiddict_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_openiddict_token_openiddict_application_application_id",
                        column: x => x.application_id,
                        principalTable: "openiddict_application",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_openiddict_token_openiddict_authorization_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "openiddict_authorization",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_application_client_id",
                table: "openiddict_application",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_authorization_application_id_status_subject_type",
                table: "openiddict_authorization",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_scope_name",
                table: "openiddict_scope",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_token_application_id_status_subject_type",
                table: "openiddict_token",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_token_authorization_id",
                table: "openiddict_token",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "ix_openiddict_token_reference_id",
                table: "openiddict_token",
                column: "reference_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_key");

            migrationBuilder.DropTable(
                name: "openiddict_scope");

            migrationBuilder.DropTable(
                name: "openiddict_token");

            migrationBuilder.DropTable(
                name: "openiddict_authorization");

            migrationBuilder.DropTable(
                name: "openiddict_application");
        }
    }
}

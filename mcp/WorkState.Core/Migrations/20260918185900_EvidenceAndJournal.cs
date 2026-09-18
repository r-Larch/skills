using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using WorkState.Core.Domain;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceAndJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifact",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    meta = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact", x => x.id);
                    table.ForeignKey(
                        name: "fk_artifact_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "attempt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    n = table.Column<int>(type: "integer", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: true),
                    agent_kind = table.Column<string>(type: "text", nullable: true),
                    session_id = table.Column<string>(type: "text", nullable: true),
                    brief_doc_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempt", x => x.id);
                    table.ForeignKey(
                        name: "fk_attempt_doc_brief_doc_id",
                        column: x => x.brief_doc_id,
                        principalTable: "doc",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_attempt_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    actor = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<ActorRole>(type: "actor_role", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event", x => x.seq);
                    table.ForeignKey(
                        name: "fk_event_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_event_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    files = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    deviations = table.Column<string>(type: "text", nullable: true),
                    risks = table.Column<string>(type: "text", nullable: true),
                    not_done = table.Column<string>(type: "text", nullable: true),
                    raw = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report", x => x.id);
                    table.ForeignKey(
                        name: "fk_report_attempt_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "attempt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command = table.Column<string>(type: "text", nullable: false),
                    result_line = table.Column<string>(type: "text", nullable: false),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    counts = table.Column<string>(type: "jsonb", nullable: true),
                    samples = table.Column<string>(type: "jsonb", nullable: true),
                    run_by = table.Column<VerifyRunner>(type: "verify_runner", nullable: false),
                    red_check = table.Column<string>(type: "jsonb", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verification", x => x.id);
                    table.ForeignKey(
                        name: "fk_verification_attempt_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "attempt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_verification_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_verification_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_criterion_evidence_verification_id",
                table: "item_criterion",
                column: "evidence_verification_id");

            migrationBuilder.CreateIndex(
                name: "artifact_item_idx",
                table: "artifact",
                columns: new[] { "item_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_attempt_brief_doc_id",
                table: "attempt",
                column: "brief_doc_id");

            migrationBuilder.CreateIndex(
                name: "ix_attempt_item_id_n",
                table: "attempt",
                columns: new[] { "item_id", "n" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "event_item_seq_idx",
                table: "event",
                columns: new[] { "item_id", "seq" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "event_ws_seq_idx",
                table: "event",
                columns: new[] { "workspace_id", "seq" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_report_attempt_id",
                table: "report",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verification_attempt_id",
                table: "verification",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "verification_cmd_idx",
                table: "verification",
                columns: new[] { "workspace_id", "command", "passed" });

            migrationBuilder.CreateIndex(
                name: "verification_item_idx",
                table: "verification",
                columns: new[] { "item_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION event_notify() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  PERFORM pg_notify('workstate', json_build_object(
                    'ws',   NEW.workspace_id,
                    'seq',  NEW.seq,
                    'item', NEW.item_id,
                    'kind', NEW.kind)::text);
                  RETURN NULL;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER event_notify_trg
                  AFTER INSERT ON event
                  FOR EACH ROW EXECUTE FUNCTION event_notify();
                """);

            // The journal is append-only (doc 02 §11). An event that can be edited is a second copy
            // of status, which is the failure this whole store exists to remove.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION event_append_only() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'the event journal is append-only: event % cannot be updated', OLD.seq
                    USING ERRCODE = 'check_violation',
                          HINT    = 'Record a correcting event instead of rewriting this one.';
                END
                $fn$;
                """);

            // UPDATE only: DELETE has to stay open so a workspace or item cascade can still clean up.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER event_append_only_trg
                  BEFORE UPDATE ON event
                  FOR EACH ROW EXECUTE FUNCTION event_append_only();
                """);

            migrationBuilder.AddForeignKey(
                name: "item_criterion_evidence_fk",
                table: "item_criterion",
                column: "evidence_verification_id",
                principalTable: "verification",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS event_append_only_trg ON event;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS event_notify_trg ON event;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS event_append_only();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS event_notify();");

            migrationBuilder.DropForeignKey(
                name: "item_criterion_evidence_fk",
                table: "item_criterion");

            migrationBuilder.DropTable(
                name: "artifact");

            migrationBuilder.DropTable(
                name: "event");

            migrationBuilder.DropTable(
                name: "report");

            migrationBuilder.DropTable(
                name: "verification");

            migrationBuilder.DropTable(
                name: "attempt");

            migrationBuilder.DropIndex(
                name: "ix_item_criterion_evidence_verification_id",
                table: "item_criterion");
        }
    }
}

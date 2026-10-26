using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Domain;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class Sessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "claim_one_writer_per_tree",
                table: "claim");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:actor_role", "controller,human,orchestrator,reviewer,system,worker")
                .Annotation("Npgsql:Enum:assumption_state", "confirmed,disproven,obsolete,open")
                .Annotation("Npgsql:Enum:constraint_kind", "command,domain_rule,generated_code,red_line,stack_fact")
                .Annotation("Npgsql:Enum:evidence_level", "asserted,code_read,measured,probed,reasoned")
                .Annotation("Npgsql:Enum:item_resolution", "accepted,duplicate,escalated,obsolete,replanned,reworked,skipped,wontfix")
                .Annotation("Npgsql:Enum:item_status", "blocked,cancelled,done,in_progress,open,ready,review,superseded")
                .Annotation("Npgsql:Enum:link_type", "blocks,derived_from,duplicates,supersedes,verifies")
                .Annotation("Npgsql:Enum:note_kind", "carry_forward,correction,decision,do_not_rederive,environment,incident,observation")
                .Annotation("Npgsql:Enum:reversal_cost", "high,irreversible,low,medium,trivial")
                .Annotation("Npgsql:Enum:verify_runner", "ci,human,orchestrator,worker")
                .Annotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:Enum:actor_role", "controller,human,orchestrator,reviewer,system,worker")
                .OldAnnotation("Npgsql:Enum:assumption_state", "confirmed,disproven,obsolete,open")
                .OldAnnotation("Npgsql:Enum:constraint_kind", "command,domain_rule,generated_code,red_line,stack_fact")
                .OldAnnotation("Npgsql:Enum:evidence_level", "asserted,code_read,measured,probed,reasoned")
                .OldAnnotation("Npgsql:Enum:item_resolution", "accepted,duplicate,escalated,obsolete,replanned,reworked,skipped,wontfix")
                .OldAnnotation("Npgsql:Enum:item_status", "blocked,cancelled,done,in_progress,open,ready,review,superseded")
                .OldAnnotation("Npgsql:Enum:link_type", "blocks,derived_from,duplicates,supersedes,verifies")
                .OldAnnotation("Npgsql:Enum:note_kind", "carry_forward,correction,decision,do_not_rederive,environment,incident,observation")
                .OldAnnotation("Npgsql:Enum:reversal_cost", "high,irreversible,low,medium,trivial")
                .OldAnnotation("Npgsql:Enum:verify_runner", "ci,human,orchestrator,worker")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "verification",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "report",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "note",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "event",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "assumption",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_session",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    claude_session_id = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<ActorRole>(type: "actor_role", nullable: false),
                    external_role = table.Column<string>(type: "text", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: true),
                    launched_by = table.Column<string>(type: "text", nullable: true),
                    cwd = table.Column<string>(type: "text", nullable: true),
                    tree = table.Column<string>(type: "text", nullable: true),
                    client_name = table.Column<string>(type: "text", nullable: true),
                    client_version = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_session_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_verification_session_id",
                table: "verification",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_session_id",
                table: "report",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_session_id",
                table: "note",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_session_id",
                table: "event",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                columns: new[] { "serialization_key", "key_workspace_id" },
                unique: true,
                filter: "released_at IS NULL AND writes AND session_id IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_claim_session_id",
                table: "claim",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_assumption_session_id",
                table: "assumption",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "agent_session_name_unique",
                table: "agent_session",
                columns: new[] { "workspace_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_assumption_agent_session_session_id",
                table: "assumption",
                column: "session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_claim_agent_session_session_id",
                table: "claim",
                column: "session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_event_agent_session_session_id",
                table: "event",
                column: "session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_note_agent_session_session_id",
                table: "note",
                column: "session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_report_agent_session_session_id",
                table: "report",
                column: "session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_verification_agent_session_session_id",
                table: "verification",
                column: "session_id",
                principalTable: "agent_session",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // One session per tree: live write claims on one tree must all share a session. A claim
            // with no session is its own holder (coalesce to its id), so it still excludes every
            // other claim on its tree. Created after claim_one_writer_per_tree, so two sessionless
            // claims still meet that index first and keep its 23505.
            migrationBuilder.Sql(
                """
                ALTER TABLE claim ADD CONSTRAINT claim_one_session_per_tree EXCLUDE USING gist (
                    serialization_key WITH =,
                    (coalesce(key_workspace_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                    (coalesce(session_id, id)) WITH <>
                ) WHERE (released_at IS NULL AND writes);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE claim DROP CONSTRAINT claim_one_session_per_tree;");

            // The restored index admits one live writer per tree, and one session may hold several.
            // The oldest claim on each tree keeps it; the rest are released, as an expiry would.
            migrationBuilder.Sql(
                """
                UPDATE claim c SET released_at = now(), release_reason = 'sessions_down'
                 WHERE c.released_at IS NULL AND c.writes
                   AND EXISTS (SELECT 1 FROM claim o
                                WHERE o.released_at IS NULL AND o.writes
                                  AND o.serialization_key = c.serialization_key
                                  AND o.key_workspace_id IS NOT DISTINCT FROM c.key_workspace_id
                                  AND (o.acquired_at, o.id) < (c.acquired_at, c.id));
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_assumption_agent_session_session_id",
                table: "assumption");

            migrationBuilder.DropForeignKey(
                name: "fk_claim_agent_session_session_id",
                table: "claim");

            migrationBuilder.DropForeignKey(
                name: "fk_event_agent_session_session_id",
                table: "event");

            migrationBuilder.DropForeignKey(
                name: "fk_note_agent_session_session_id",
                table: "note");

            migrationBuilder.DropForeignKey(
                name: "fk_report_agent_session_session_id",
                table: "report");

            migrationBuilder.DropForeignKey(
                name: "fk_verification_agent_session_session_id",
                table: "verification");

            migrationBuilder.DropTable(
                name: "agent_session");

            migrationBuilder.DropIndex(
                name: "ix_verification_session_id",
                table: "verification");

            migrationBuilder.DropIndex(
                name: "ix_report_session_id",
                table: "report");

            migrationBuilder.DropIndex(
                name: "ix_note_session_id",
                table: "note");

            migrationBuilder.DropIndex(
                name: "ix_event_session_id",
                table: "event");

            migrationBuilder.DropIndex(
                name: "claim_one_writer_per_tree",
                table: "claim");

            migrationBuilder.DropIndex(
                name: "ix_claim_session_id",
                table: "claim");

            migrationBuilder.DropIndex(
                name: "ix_assumption_session_id",
                table: "assumption");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "verification");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "report");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "note");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "event");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "assumption");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:actor_role", "controller,human,orchestrator,reviewer,system,worker")
                .Annotation("Npgsql:Enum:assumption_state", "confirmed,disproven,obsolete,open")
                .Annotation("Npgsql:Enum:constraint_kind", "command,domain_rule,generated_code,red_line,stack_fact")
                .Annotation("Npgsql:Enum:evidence_level", "asserted,code_read,measured,probed,reasoned")
                .Annotation("Npgsql:Enum:item_resolution", "accepted,duplicate,escalated,obsolete,replanned,reworked,skipped,wontfix")
                .Annotation("Npgsql:Enum:item_status", "blocked,cancelled,done,in_progress,open,ready,review,superseded")
                .Annotation("Npgsql:Enum:link_type", "blocks,derived_from,duplicates,supersedes,verifies")
                .Annotation("Npgsql:Enum:note_kind", "carry_forward,correction,decision,do_not_rederive,environment,incident,observation")
                .Annotation("Npgsql:Enum:reversal_cost", "high,irreversible,low,medium,trivial")
                .Annotation("Npgsql:Enum:verify_runner", "ci,human,orchestrator,worker")
                .Annotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:Enum:actor_role", "controller,human,orchestrator,reviewer,system,worker")
                .OldAnnotation("Npgsql:Enum:assumption_state", "confirmed,disproven,obsolete,open")
                .OldAnnotation("Npgsql:Enum:constraint_kind", "command,domain_rule,generated_code,red_line,stack_fact")
                .OldAnnotation("Npgsql:Enum:evidence_level", "asserted,code_read,measured,probed,reasoned")
                .OldAnnotation("Npgsql:Enum:item_resolution", "accepted,duplicate,escalated,obsolete,replanned,reworked,skipped,wontfix")
                .OldAnnotation("Npgsql:Enum:item_status", "blocked,cancelled,done,in_progress,open,ready,review,superseded")
                .OldAnnotation("Npgsql:Enum:link_type", "blocks,derived_from,duplicates,supersedes,verifies")
                .OldAnnotation("Npgsql:Enum:note_kind", "carry_forward,correction,decision,do_not_rederive,environment,incident,observation")
                .OldAnnotation("Npgsql:Enum:reversal_cost", "high,irreversible,low,medium,trivial")
                .OldAnnotation("Npgsql:Enum:verify_runner", "ci,human,orchestrator,worker")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                columns: new[] { "serialization_key", "key_workspace_id" },
                unique: true,
                filter: "released_at IS NULL AND writes")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}

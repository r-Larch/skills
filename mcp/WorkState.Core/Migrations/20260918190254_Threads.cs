using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WorkState.Core.Domain;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class Threads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_token",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<ActorRole>(type: "actor_role", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_token_item_scope_item_id",
                        column: x => x.scope_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_token_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assumption",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ref_scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: false),
                    statement = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<EvidenceLevel>(type: "evidence_level", nullable: false),
                    reversal = table.Column<ReversalCost>(type: "reversal_cost", nullable: false),
                    revisit_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revisit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<AssumptionState>(type: "assumption_state", nullable: false, defaultValue: AssumptionState.Open),
                    resolution_note = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assumption", x => x.id);
                    table.CheckConstraint("assumption_resolved", "(state = 'open') = (resolved_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_assumption_item_ref_scope_id",
                        column: x => x.ref_scope_id,
                        principalTable: "item",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_assumption_item_revisit_item_id",
                        column: x => x.revisit_item_id,
                        principalTable: "item",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_assumption_item_scope_item_id",
                        column: x => x.scope_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assumption_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "constraint_rule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<ConstraintKind>(type: "constraint_kind", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    route = table.Column<string>(type: "text", nullable: true),
                    covers_risk = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    severity = table.Column<string>(type: "text", nullable: false, defaultValue: "binding"),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    added_by_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_constraint_rule", x => x.id);
                    table.ForeignKey(
                        name: "fk_constraint_rule_item_added_by_item_id",
                        column: x => x.added_by_item_id,
                        principalTable: "item",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_constraint_rule_item_scope_item_id",
                        column: x => x.scope_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_constraint_rule_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<NoteKind>(type: "note_kind", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    to_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_selector = table.Column<string>(type: "jsonb", nullable: true),
                    corrects_note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    corrects_block_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_note", x => x.id);
                    table.ForeignKey(
                        name: "fk_note_attempt_consumed_by",
                        column: x => x.consumed_by,
                        principalTable: "attempt",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_note_doc_block_corrects_block_id",
                        column: x => x.corrects_block_id,
                        principalTable: "doc_block",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_note_item_from_item_id",
                        column: x => x.from_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_note_item_to_item_id",
                        column: x => x.to_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_note_note_corrects_note_id",
                        column: x => x.corrects_note_id,
                        principalTable: "note",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_note_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "claim",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serialization_key = table.Column<string>(type: "text", nullable: false),
                    writes = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    agent_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<ActorRole>(type: "actor_role", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    release_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim", x => x.id);
                    table.ForeignKey(
                        name: "fk_claim_agent_tokens_agent_token_id",
                        column: x => x.agent_token_id,
                        principalTable: "agent_token",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_claim_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "agent_token_hash_idx",
                table: "agent_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_token_scope_item_id",
                table: "agent_token",
                column: "scope_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_token_workspace_id",
                table: "agent_token",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "assumption_open_idx",
                table: "assumption",
                column: "workspace_id",
                filter: "state = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_assumption_ref_scope_id",
                table: "assumption",
                column: "ref_scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_assumption_revisit_item_id",
                table: "assumption",
                column: "revisit_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_assumption_scope_item_id",
                table: "assumption",
                column: "scope_item_id");

            migrationBuilder.CreateIndex(
                name: "claim_one_live_per_item",
                table: "claim",
                column: "item_id",
                unique: true,
                filter: "released_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                column: "serialization_key",
                unique: true,
                filter: "released_at IS NULL AND writes");

            migrationBuilder.CreateIndex(
                name: "ix_claim_agent_token_id",
                table: "claim",
                column: "agent_token_id");

            migrationBuilder.CreateIndex(
                name: "constraint_risk_idx",
                table: "constraint_rule",
                column: "covers_risk")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "constraint_scope_idx",
                table: "constraint_rule",
                columns: new[] { "workspace_id", "scope_item_id" },
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_constraint_rule_added_by_item_id",
                table: "constraint_rule",
                column: "added_by_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_constraint_rule_scope_item_id",
                table: "constraint_rule",
                column: "scope_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_consumed_by",
                table: "note",
                column: "consumed_by");

            migrationBuilder.CreateIndex(
                name: "ix_note_corrects_block_id",
                table: "note",
                column: "corrects_block_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_corrects_note_id",
                table: "note",
                column: "corrects_note_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_from_item_id",
                table: "note",
                column: "from_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_to_item_id",
                table: "note",
                column: "to_item_id");

            migrationBuilder.CreateIndex(
                name: "note_pending_idx",
                table: "note",
                columns: new[] { "workspace_id", "to_item_id" },
                filter: "consumed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "note_selector_idx",
                table: "note",
                column: "to_selector",
                filter: "consumed_at IS NULL")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            // doc 02 §7, corrected 2026-09-18: an assumption ref is numbered within its run, not
            // globally — otherwise 'A1' could exist only once across the whole server and the second
            // parallel orchestrator could never open one.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION assumption_scope_before() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_kind      text;
                  v_ancestors uuid[];
                BEGIN
                  IF NEW.scope_item_id IS NULL THEN
                    NEW.ref_scope_id := NULL;
                    RETURN NEW;
                  END IF;

                  SELECT i.kind, i.ancestors INTO v_kind, v_ancestors
                    FROM item i WHERE i.id = NEW.scope_item_id;

                  NEW.ref_scope_id := item_scope_of(v_kind, v_ancestors, NEW.scope_item_id);
                  RETURN NEW;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER assumption_scope_before_trg
                  BEFORE INSERT OR UPDATE ON assumption
                  FOR EACH ROW EXECUTE FUNCTION assumption_scope_before();
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE assumption
                  ADD CONSTRAINT assumption_ref_unique UNIQUE NULLS NOT DISTINCT (ref_scope_id, ref);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE assumption DROP CONSTRAINT IF EXISTS assumption_ref_unique;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS assumption_scope_before_trg ON assumption;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS assumption_scope_before();");

            migrationBuilder.DropTable(
                name: "assumption");

            migrationBuilder.DropTable(
                name: "claim");

            migrationBuilder.DropTable(
                name: "constraint_rule");

            migrationBuilder.DropTable(
                name: "note");

            migrationBuilder.DropTable(
                name: "agent_token");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using WorkState.Core.Domain;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "item_kind",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    allowed_parent_kinds = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    scopes_refs = table.Column<bool>(type: "boolean", nullable: false),
                    requires_verify = table.Column<bool>(type: "boolean", nullable: false),
                    requires_criteria = table.Column<bool>(type: "boolean", nullable: false),
                    requires_commit = table.Column<bool>(type: "boolean", nullable: false),
                    is_gateable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_kind", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false, defaultValue: "default"),
                    settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "doc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_doc", x => x.id);
                    table.ForeignKey(
                        name: "fk_doc_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "doc_block",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<decimal>(type: "numeric", nullable: false),
                    short_id = table.Column<string>(type: "text", nullable: false),
                    heading = table.Column<string>(type: "text", nullable: true),
                    heading_level = table.Column<short>(type: "smallint", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    search = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('english', coalesce(heading, '')), 'A') ||\nsetweight(to_tsvector('english', body), 'B')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_doc_block", x => x.id);
                    table.ForeignKey(
                        name: "fk_doc_block_doc_doc_id",
                        column: x => x.doc_id,
                        principalTable: "doc",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: false),
                    ref_scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    seq = table.Column<decimal>(type: "numeric", nullable: false),
                    ancestors = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    depth = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "coalesce(array_length(ancestors, 1), 0)", stored: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    intent = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<ItemStatus>(type: "item_status", nullable: false, defaultValue: ItemStatus.Open),
                    resolution = table.Column<ItemResolution>(type: "item_resolution", nullable: true),
                    priority = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    verify_cmd = table.Column<string>(type: "text", nullable: true),
                    risk_tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    review_required = table.Column<bool>(type: "boolean", nullable: false, computedColumnSql: "risk_tags <> '{}'::text[]", stored: true),
                    gate_criterion = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    body_doc_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<string>(type: "text", nullable: true),
                    search = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('simple', coalesce(ref, '')), 'A') ||\nsetweight(to_tsvector('english', coalesce(title, '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(intent, '')), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item", x => x.id);
                    table.CheckConstraint("item_closed_at_set", "(status IN ('done','cancelled','superseded')) = (closed_at IS NOT NULL)");
                    table.CheckConstraint("item_resolution_only_when_done", "(status = 'done') = (resolution IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_item_docs_body_doc_id",
                        column: x => x.body_doc_id,
                        principalTable: "doc",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_item_item_kind_kind",
                        column: x => x.kind,
                        principalTable: "item_kind",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_item_parent_id",
                        column: x => x.parent_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_item_ref_scope_id",
                        column: x => x.ref_scope_id,
                        principalTable: "item",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_item_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "doc_block_revision",
                columns: table => new
                {
                    block_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    heading = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    actor = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_doc_block_revision", x => new { x.block_id, x.revision });
                    table.ForeignKey(
                        name: "fk_doc_block_revision_doc_block_block_id",
                        column: x => x.block_id,
                        principalTable: "doc_block",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_criterion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    @checked = table.Column<bool>(name: "checked", type: "boolean", nullable: false),
                    evidence = table.Column<string>(type: "text", nullable: true),
                    evidence_verification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_criterion", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_criterion_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_link",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<LinkType>(type: "link_type", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_link", x => x.id);
                    table.CheckConstraint("item_link_no_self", "from_item_id <> to_item_id");
                    table.ForeignKey(
                        name: "fk_item_link_item_from_item_id",
                        column: x => x.from_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_link_item_to_item_id",
                        column: x => x.to_item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_link_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_config",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_root = table.Column<string>(type: "text", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: false, defaultValue: "master"),
                    verify_catalog = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_config", x => x.item_id);
                    table.ForeignKey(
                        name: "fk_project_config_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_config",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: true),
                    worktree_path = table.Column<string>(type: "text", nullable: true),
                    serialization_key = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false, defaultValue: "full"),
                    owner_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_config", x => x.item_id);
                    table.ForeignKey(
                        name: "fk_run_config_item_item_id",
                        column: x => x.item_id,
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_run_config_item_project_item_id",
                        column: x => x.project_item_id,
                        principalTable: "item",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_doc_item_id",
                table: "doc",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_doc_workspace_id_item_id_slug",
                table: "doc",
                columns: new[] { "workspace_id", "item_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "doc_block_search_idx",
                table: "doc_block",
                column: "search")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_doc_block_doc_id_short_id",
                table: "doc_block",
                columns: new[] { "doc_id", "short_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "item_open_idx",
                table: "item",
                columns: new[] { "workspace_id", "status", "seq" },
                filter: "status IN ('open','ready','in_progress','blocked','review')");

            migrationBuilder.CreateIndex(
                name: "item_parent_seq_idx",
                table: "item",
                columns: new[] { "parent_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "item_ref_trgm_idx",
                table: "item",
                column: "ref")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "item_risk_idx",
                table: "item",
                column: "risk_tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "item_search_idx",
                table: "item",
                column: "search")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "item_subtree_idx",
                table: "item",
                column: "ancestors")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_item_body_doc_id",
                table: "item",
                column: "body_doc_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_kind",
                table: "item",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ix_item_ref_scope_id",
                table: "item",
                column: "ref_scope_id");

            migrationBuilder.CreateIndex(
                name: "item_criterion_open_idx",
                table: "item_criterion",
                column: "item_id",
                filter: "NOT checked");

            migrationBuilder.CreateIndex(
                name: "ix_item_criterion_item_id_seq",
                table: "item_criterion",
                columns: new[] { "item_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "item_link_from_idx",
                table: "item_link",
                columns: new[] { "from_item_id", "type" });

            migrationBuilder.CreateIndex(
                name: "item_link_to_idx",
                table: "item_link",
                columns: new[] { "to_item_id", "type" });

            migrationBuilder.CreateIndex(
                name: "item_link_unique",
                table: "item_link",
                columns: new[] { "from_item_id", "to_item_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_link_workspace_id",
                table: "item_link",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_config_project_item_id",
                table: "run_config",
                column: "project_item_id");

            migrationBuilder.CreateIndex(
                name: "run_serialization_idx",
                table: "run_config",
                column: "serialization_key");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_slug",
                table: "workspace",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_doc_item_item_id",
                table: "doc",
                column: "item_id",
                principalTable: "item",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // ---- Below here: what the EF model cannot express. Deferrable uniqueness, the tree
            // ---- triggers, and the kind ladder the triggers read.

            // NULLS NOT DISTINCT extends audit check #3 to items that sit above any ref-scoping
            // ancestor; without it a null ref_scope_id would exempt them from the unique altogether.
            migrationBuilder.Sql(
                """
                ALTER TABLE item
                  ADD CONSTRAINT item_ref_unique UNIQUE NULLS NOT DISTINCT (ref_scope_id, ref);
                """);

            // Deferrable, because a reorder swaps two seq values inside one transaction.
            migrationBuilder.Sql(
                """
                ALTER TABLE item
                  ADD CONSTRAINT item_sibling_order UNIQUE (parent_id, seq) DEFERRABLE INITIALLY IMMEDIATE;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE doc_block
                  ADD CONSTRAINT doc_block_seq_unique UNIQUE (doc_id, seq) DEFERRABLE INITIALLY IMMEDIATE;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO item_kind
                    (key, label, rank, allowed_parent_kinds, scopes_refs,
                     requires_verify, requires_criteria, requires_commit, is_gateable)
                VALUES
                    ('project',  'Project',   0, '{}',                                                  true,  false, false, false, false),
                    ('feature',  'Feature',  10, '{project}',                                           false, false, false, false, false),
                    ('run',      'Run',      20, '{project,feature}',                                   true,  false, false, false, false),
                    ('phase',    'Phase',    30, '{run}',                                               false, false, false, false, true),
                    ('task',     'Task',     40, '{phase,run}',                                         false, true,  true,  false, false),
                    ('subtask',  'Sub-task', 50, '{task}',                                              false, true,  false, false, false),
                    ('finding',  'Finding',  60, '{project,feature,run,phase,task,subtask}',            false, false, false, false, false),
                    ('followup', 'Follow-up',60, '{project,feature,run,phase,task,subtask}',            false, false, false, false, false),
                    ('question', 'Question', 60, '{project,feature,run,phase,task,subtask}',            false, false, false, false, false),
                    ('incident', 'Incident', 60, '{project,feature,run,phase,task,subtask}',            false, false, false, false, false),
                    ('spike',    'Spike',    60, '{project,feature,run,phase,task,subtask}',            false, false, false, false, false)
                ON CONFLICT (key) DO NOTHING;
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION item_scope_of(p_kind text, p_ancestors uuid[], p_self uuid)
                RETURNS uuid LANGUAGE sql STABLE AS $fn$
                  SELECT CASE
                    WHEN (SELECT k.scopes_refs FROM item_kind k WHERE k.key = p_kind) THEN p_self
                    ELSE (SELECT a.id
                            FROM unnest(p_ancestors) WITH ORDINALITY AS u(id, ord)
                            JOIN item a       ON a.id  = u.id
                            JOIN item_kind ak ON ak.key = a.kind
                           WHERE ak.scopes_refs
                           ORDER BY u.ord DESC
                           LIMIT 1)
                  END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION item_tree_before() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_ancestors uuid[];
                BEGIN
                  IF TG_OP = 'UPDATE'
                     AND NEW.parent_id IS NOT DISTINCT FROM OLD.parent_id
                     AND NEW.kind      IS NOT DISTINCT FROM OLD.kind THEN
                    RETURN NEW;
                  END IF;

                  IF NEW.parent_id IS NULL THEN
                    v_ancestors := '{}'::uuid[];
                  ELSE
                    IF NEW.parent_id = NEW.id THEN
                      RAISE EXCEPTION 'item % cannot be its own parent', NEW.id
                        USING ERRCODE = 'check_violation';
                    END IF;

                    SELECT p.ancestors || p.id INTO v_ancestors FROM item p WHERE p.id = NEW.parent_id;

                    IF v_ancestors IS NULL THEN
                      RAISE EXCEPTION 'parent item % does not exist', NEW.parent_id
                        USING ERRCODE = 'foreign_key_violation';
                    END IF;

                    IF NEW.id = ANY (v_ancestors) THEN
                      RAISE EXCEPTION 'reparenting % under % would make the tree cyclic', NEW.id, NEW.parent_id
                        USING ERRCODE = 'check_violation',
                              HINT    = 'The chosen parent is inside this item''s own subtree. Move it out first, or pick a parent elsewhere.';
                    END IF;
                  END IF;

                  NEW.ancestors    := v_ancestors;
                  NEW.ref_scope_id := item_scope_of(NEW.kind, v_ancestors, NEW.id);
                  RETURN NEW;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION item_tree_after() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF pg_trigger_depth() > 1 THEN
                    RETURN NULL;
                  END IF;

                  WITH moved AS (
                    SELECT d.id,
                           NEW.ancestors || NEW.id ||
                           d.ancestors[array_position(d.ancestors, NEW.id) + 1 : array_length(d.ancestors, 1)] AS anc
                      FROM item d
                     WHERE d.ancestors @> ARRAY[NEW.id]
                  )
                  UPDATE item d
                     SET ancestors    = m.anc,
                         ref_scope_id = item_scope_of(d.kind, m.anc, d.id)
                    FROM moved m
                   WHERE m.id = d.id;

                  RETURN NULL;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER item_tree_before_trg
                  BEFORE INSERT OR UPDATE ON item
                  FOR EACH ROW EXECUTE FUNCTION item_tree_before();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER item_tree_after_trg
                  AFTER UPDATE ON item
                  FOR EACH ROW WHEN (NEW.ancestors IS DISTINCT FROM OLD.ancestors)
                  EXECUTE FUNCTION item_tree_after();
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION item_link_no_blocks_cycle() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_path uuid[];
                  v_refs text;
                BEGIN
                  IF NEW.type <> 'blocks' THEN
                    RETURN NEW;
                  END IF;

                  WITH RECURSIVE reach(id, path) AS (
                    SELECT NEW.to_item_id, ARRAY[NEW.to_item_id]
                    UNION ALL
                    SELECT l.to_item_id, r.path || l.to_item_id
                      FROM item_link l
                      JOIN reach r ON l.from_item_id = r.id
                     WHERE l.type = 'blocks'
                       AND l.id <> NEW.id
                       AND NOT (l.to_item_id = ANY (r.path))
                  )
                  SELECT ARRAY[NEW.from_item_id] || r.path INTO v_path
                    FROM reach r WHERE r.id = NEW.from_item_id LIMIT 1;

                  IF v_path IS NOT NULL THEN
                    SELECT string_agg(i.ref, ' -> ' ORDER BY u.ord) INTO v_refs
                      FROM unnest(v_path) WITH ORDINALITY AS u(id, ord)
                      JOIN item i ON i.id = u.id;
                    RAISE EXCEPTION 'blocks cycle refused: %', v_refs
                      USING ERRCODE = 'check_violation',
                            HINT    = 'Remove one blocks edge on that path, or record the relation as derived_from if it is rework lineage rather than an ordering dependency.';
                  END IF;

                  RETURN NEW;
                END
                $fn$;
                """);

            // doc 02 §4: "supersedes sets the target's status = 'superseded' in the same transaction".
            migrationBuilder.Sql(
                """
                CREATE FUNCTION item_link_apply_supersedes() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.type = 'supersedes' THEN
                    UPDATE item
                       SET status    = 'superseded',
                           closed_at = coalesce(closed_at, now())
                     WHERE id = NEW.to_item_id
                       AND status NOT IN ('done','cancelled','superseded');
                  END IF;
                  RETURN NULL;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER item_link_no_blocks_cycle_trg
                  BEFORE INSERT OR UPDATE ON item_link
                  FOR EACH ROW EXECUTE FUNCTION item_link_no_blocks_cycle();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER item_link_apply_supersedes_trg
                  AFTER INSERT ON item_link
                  FOR EACH ROW EXECUTE FUNCTION item_link_apply_supersedes();
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION doc_block_revise() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.heading IS NOT DISTINCT FROM OLD.heading
                     AND NEW.body IS NOT DISTINCT FROM OLD.body THEN
                    RETURN NEW;
                  END IF;

                  INSERT INTO doc_block_revision (block_id, revision, heading, body, at, actor)
                  VALUES (OLD.id, OLD.revision, OLD.heading, OLD.body, now(),
                          current_setting('workstate.actor', true));

                  NEW.revision   := OLD.revision + 1;
                  NEW.updated_at := now();
                  RETURN NEW;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER doc_block_revise_trg
                  BEFORE UPDATE ON doc_block
                  FOR EACH ROW EXECUTE FUNCTION doc_block_revise();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS doc_block_revise_trg ON doc_block;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS item_link_apply_supersedes_trg ON item_link;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS item_link_no_blocks_cycle_trg ON item_link;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS item_tree_after_trg ON item;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS item_tree_before_trg ON item;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS doc_block_revise();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS item_link_apply_supersedes();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS item_link_no_blocks_cycle();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS item_tree_after();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS item_tree_before();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS item_scope_of(text, uuid[], uuid);");

            migrationBuilder.DropForeignKey(
                name: "fk_doc_item_item_id",
                table: "doc");

            migrationBuilder.DropTable(
                name: "doc_block_revision");

            migrationBuilder.DropTable(
                name: "item_criterion");

            migrationBuilder.DropTable(
                name: "item_link");

            migrationBuilder.DropTable(
                name: "project_config");

            migrationBuilder.DropTable(
                name: "run_config");

            migrationBuilder.DropTable(
                name: "doc_block");

            migrationBuilder.DropTable(
                name: "item");

            migrationBuilder.DropTable(
                name: "doc");

            migrationBuilder.DropTable(
                name: "item_kind");

            migrationBuilder.DropTable(
                name: "workspace");
        }
    }
}

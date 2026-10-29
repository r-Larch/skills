using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 §project_repo, doc 07 §5. A path names a folder on one person's machine, so a <c>root</c> binding and a
    /// path tree lock are per user; a remote and a name lock stay per workspace. A credential with no user keeps NULL,
    /// which is what every row was until now.
    /// <para>
    /// Every existing root binding is copied to every user who can see its project — its members, or every member of
    /// the workspace when it has none — and the NULL row stays, so nothing that resolved before stops resolving: a
    /// user finds their copy, a credential with no user the original.
    /// </para>
    /// </summary>
    public partial class MachineKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "project_repo_key_unique",
                table: "project_repo");

            migrationBuilder.DropIndex(
                name: "claim_one_writer_per_tree",
                table: "claim");

            migrationBuilder.Sql("ALTER TABLE claim DROP CONSTRAINT claim_one_session_per_tree;");

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "project_repo",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "key_user_id",
                table: "claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_repo_user_id_workspace_id",
                table: "project_repo",
                columns: new[] { "user_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "project_repo_key_unique",
                table: "project_repo",
                columns: new[] { "workspace_id", "user_id", "key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                columns: new[] { "serialization_key", "key_workspace_id", "key_user_id" },
                unique: true,
                filter: "released_at IS NULL AND writes AND session_id IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_project_repo_tenant_membership_user_id_workspace_id",
                table: "project_repo",
                columns: new[] { "user_id", "workspace_id" },
                principalTable: "tenant_membership",
                principalColumns: new[] { "user_id", "workspace_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql(
                """
                INSERT INTO project_repo (id, workspace_id, project_item_id, user_id, kind, key, added_at)
                SELECT gen_random_uuid(), r.workspace_id, r.project_item_id, u.user_id, r.kind, r.key, r.added_at
                FROM   project_repo r
                CROSS  JOIN LATERAL (
                         SELECT m.user_id FROM project_member m WHERE m.project_item_id = r.project_item_id
                         UNION
                         SELECT t.user_id FROM tenant_membership t
                         WHERE  t.workspace_id = r.workspace_id
                           AND  NOT EXISTS (SELECT 1 FROM project_member m WHERE m.project_item_id = r.project_item_id)
                       ) u
                WHERE  r.kind = 'root' AND r.user_id IS NULL;
                """);

            // A live claim on a path, taken by a session that has a user, is that user's from here on. A path is what
            // WorkingTree.Parse makes of one: a lowercased drive, or a leading slash.
            migrationBuilder.Sql(
                """
                UPDATE claim c
                   SET key_user_id = s.user_id
                  FROM agent_session s
                 WHERE s.id = c.session_id
                   AND c.released_at IS NULL
                   AND c.serialization_key ~ '^([a-z]:/|/)';
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE claim ADD CONSTRAINT claim_one_session_per_tree EXCLUDE USING gist (
                    serialization_key WITH =,
                    (coalesce(key_workspace_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                    (coalesce(key_user_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                    (coalesce(session_id, id)) WITH <>
                ) WHERE (released_at IS NULL AND writes);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE claim DROP CONSTRAINT claim_one_session_per_tree;");

            // The restored keys hold a path once per workspace. The oldest live claim on each path keeps it; the rest
            // are released, as an expiry would.
            migrationBuilder.Sql(
                """
                UPDATE claim c SET released_at = now(), release_reason = 'machine_keys_down'
                 WHERE c.released_at IS NULL AND c.writes
                   AND EXISTS (SELECT 1 FROM claim o
                                WHERE o.released_at IS NULL AND o.writes
                                  AND o.serialization_key = c.serialization_key
                                  AND o.key_workspace_id IS NOT DISTINCT FROM c.key_workspace_id
                                  AND coalesce(o.session_id, o.id) <> coalesce(c.session_id, c.id)
                                  AND (o.acquired_at, o.id) < (c.acquired_at, c.id));
                """);

            // One root per workspace again: a NULL row keeps its key, else the oldest user's.
            migrationBuilder.Sql(
                """
                DELETE FROM project_repo r
                 USING (SELECT id, row_number() OVER (PARTITION BY workspace_id, key
                                                      ORDER BY user_id IS NOT NULL, added_at, id) AS n
                          FROM project_repo) d
                 WHERE d.id = r.id AND d.n > 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_project_repo_tenant_membership_user_id_workspace_id",
                table: "project_repo");

            migrationBuilder.DropIndex(
                name: "ix_project_repo_user_id_workspace_id",
                table: "project_repo");

            migrationBuilder.DropIndex(
                name: "project_repo_key_unique",
                table: "project_repo");

            migrationBuilder.DropIndex(
                name: "claim_one_writer_per_tree",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "project_repo");

            migrationBuilder.DropColumn(
                name: "key_user_id",
                table: "claim");

            migrationBuilder.CreateIndex(
                name: "project_repo_key_unique",
                table: "project_repo",
                columns: new[] { "workspace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "claim_one_writer_per_tree",
                table: "claim",
                columns: new[] { "serialization_key", "key_workspace_id" },
                unique: true,
                filter: "released_at IS NULL AND writes AND session_id IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.Sql(
                """
                ALTER TABLE claim ADD CONSTRAINT claim_one_session_per_tree EXCLUDE USING gist (
                    serialization_key WITH =,
                    (coalesce(key_workspace_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
                    (coalesce(session_id, id)) WITH <>
                ) WHERE (released_at IS NULL AND writes);
                """);
        }
    }
}

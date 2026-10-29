using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkState.Core.Migrations
{
    /// <summary>
    /// doc 02 "Kinds are data". A task may sit under a task: tasks nest in practice, each keeping its own criteria and verify
    /// guard, and <c>allowed_parent_kinds</c> is the one rule create, move and import obey. InitialTree is the only seed of
    /// <c>item_kind</c>, so a fresh database reaches the same row through this migration.
    /// </summary>
    public partial class TaskUnderTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE item_kind SET allowed_parent_kinds = '{phase,run,task}' WHERE key = 'task';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE item_kind SET allowed_parent_kinds = '{phase,run}' WHERE key = 'task';");
        }
    }
}

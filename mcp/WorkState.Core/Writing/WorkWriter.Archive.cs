using Dapper;
using Npgsql;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>What the dashboard's archive endpoint answers with.</summary>
public sealed record ArchiveOutcome(Guid Id, string Ref, string Kind, DateTimeOffset? ArchivedAt, bool Changed);

public sealed partial class WorkWriter
{
    /// <summary>
    /// doc 02 §2 — archiving hides a project from the default lists and deletes nothing. The
    /// dashboard's write, by id; <c>work_update(archived:)</c> is the same write by ref. Null when no
    /// item has that id in the workspace.
    /// </summary>
    public async Task<ArchiveOutcome?> SetArchivedAsync(
        Guid workspaceId,
        Actor actor,
        Guid itemId,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var item = await connection.QuerySingleOrDefaultAsync<(Guid Id, Guid WorkspaceId, string Ref, string Kind)>(
            new CommandDefinition(
                "SELECT id, workspace_id, ref, kind FROM item WHERE id = @id AND workspace_id = @workspace;",
                new { id = itemId, workspace = workspaceId },
                transaction,
                cancellationToken: cancellationToken));

        if (item.Id == Guid.Empty)
        {
            return null;
        }

        if (item.Kind != "project")
        {
            return new ArchiveOutcome(item.Id, item.Ref, item.Kind, null, false);
        }

        var (at, changed) = await ArchiveRowAsync(
            connection, transaction, item.WorkspaceId, item.Id, item.Ref, archived, actor, "dashboard", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new ArchiveOutcome(item.Id, item.Ref, item.Kind, at, changed);
    }

    /// <summary>
    /// Sets or clears <c>archived_at</c> and journals the change; a call that changes nothing
    /// journals nothing, so the first archive keeps its timestamp.
    /// </summary>
    private static async Task<(DateTimeOffset? At, bool Changed)> ArchiveRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        string projectRef,
        bool archived,
        Actor actor,
        string via,
        CancellationToken cancellationToken)
    {
        var before = await InstantAsync(
            connection, transaction, "SELECT archived_at FROM item WHERE id = @id FOR UPDATE;", projectId, null, cancellationToken);

        if ((before is not null) == archived)
        {
            return (before, false);
        }

        var at = await InstantAsync(
            connection,
            transaction,
            "UPDATE item SET archived_at = CASE WHEN @archived THEN now() END WHERE id = @id RETURNING archived_at;",
            projectId,
            archived,
            cancellationToken);

        await EventAsync(
            connection, transaction, workspaceId, projectId, actor, archived ? "archived" : "unarchived",
            $"{projectRef} {(archived ? "archived" : "unarchived")} by {actor.Label}",
            new { via, by = actor.Label },
            cancellationToken);

        return (at, true);
    }

    /// <summary>
    /// New work is the owner saying the project is live again: the archived project above a created
    /// item comes back, journalled, in the same transaction as the item.
    /// </summary>
    private static async Task<IReadOnlyList<string>> UnarchiveForNewWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid createdId,
        string createdRef,
        Actor actor,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE item p
               SET archived_at = NULL
              FROM item c
             WHERE c.id = @created
               AND p.id = ANY(c.ancestors)
               AND p.kind = 'project'
               AND p.archived_at IS NOT NULL
            RETURNING p.id AS "Id", p.ref AS "Ref";
            """;

        var restored = (await connection.QueryAsync<(Guid Id, string Ref)>(new CommandDefinition(
            sql, new { created = createdId }, transaction, cancellationToken: cancellationToken))).AsList();

        foreach (var project in restored)
        {
            await EventAsync(
                connection, transaction, workspaceId, project.Id, actor, "unarchived",
                $"{project.Ref} unarchived: new work {createdRef} created under it",
                new { via = "work_create", created = createdRef, by = actor.Label },
                cancellationToken);
        }

        return [.. restored.Select(p => p.Ref)];
    }

    private static async Task<DateTimeOffset?> InstantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid id,
        bool? archived,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        if (archived is { } flag)
        {
            command.Parameters.AddWithValue("archived", flag);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && !await reader.IsDBNullAsync(0, cancellationToken)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : null;
    }

    private static string NotArchivable(string reference, string kind)
        => Refusal.Blocked(
            $"work_update({reference}, archived:…) — {reference} is a {kind}; only a project is archived.",
            "archiving hides a whole project from the dashboard's lists; a run or task is closed instead",
            $"work_status(ref:\"{reference}\", to:\"done\") or to:\"cancelled\" to close it",
            "work_update(ref:\"<the project's ref>\", archived:true) to hide its project");
}

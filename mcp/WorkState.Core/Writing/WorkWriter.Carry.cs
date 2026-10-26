using Dapper;
using Npgsql;
using WorkState.Core.Domain;

namespace WorkState.Core.Writing;

public sealed partial class WorkWriter
{
    public const string BacklogRunTitle = "Backlog — work deferred out of the project's runs";

    private sealed record Carried(Guid Id, string Ref);

    private sealed record OpenChild(Guid Id, string Ref, string Kind);

    /// <summary>
    /// doc 07 §3, condition 3, ws/12. Before ws/12, deferring to "backlog" made a backlog phase
    /// inside the run, and that phase then kept the run from closing. Runs that still hold one are
    /// closable: when it is the only open child, the close carries its open items to the
    /// project-level backlog run and closes the emptied phase. Any other open child still refuses,
    /// and a refusal later in the guard rolls this back with the rest of the transaction.
    /// </summary>
    private static async Task<string?> CarryBacklogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow run,
        Actor actor,
        CancellationToken cancellationToken)
    {
        const string openChildrenSql =
            """
            SELECT id AS "Id", ref AS "Ref", kind AS "Kind"
            FROM   item
            WHERE  parent_id = @run
              AND  kind <> 'finding'
              AND  status NOT IN ('done','cancelled','superseded');
            """;

        var open = (await connection.QueryAsync<OpenChild>(new CommandDefinition(
            openChildrenSql, new { run = run.Id }, transaction, cancellationToken: cancellationToken))).AsList();

        if (open is not [{ Kind: "phase" } backlog]
            || !backlog.Ref.Equals("backlog", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string carriedSql =
            """
            SELECT id AS "Id", ref AS "Ref"
            FROM   item
            WHERE  parent_id = @phase
              AND  status NOT IN ('done','cancelled','superseded')
            ORDER  BY seq;
            """;

        var items = (await connection.QueryAsync<Carried>(new CommandDefinition(
            carriedSql, new { phase = backlog.Id }, transaction, cancellationToken: cancellationToken))).AsList();

        var moved = new List<string>();
        string? target = null;

        if (items.Count > 0)
        {
            var holder = await BacklogRunAsync(connection, transaction, run.WorkspaceId, run.Id, cancellationToken);
            var targetId = holder.Id;
            target = holder.Ref;

            foreach (var carried in items)
            {
                var landed = await FreeRefAsync(
                    connection,
                    transaction,
                    targetId,
                    n => n == 1 ? carried.Ref : $"{carried.Ref}{n}",
                    cancellationToken);

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE item
                       SET parent_id = @parent, ref = @ref, seq = @seq, revision = revision + 1
                     WHERE id = @id;
                    """,
                    new
                    {
                        id = carried.Id,
                        parent = targetId,
                        @ref = landed,
                        seq = await NextSeqAsync(connection, transaction, targetId, cancellationToken),
                    },
                    transaction,
                    cancellationToken: cancellationToken));

                moved.Add(landed == carried.Ref ? landed : $"{carried.Ref} as {landed}");
            }
        }

        await ApplyAsync(
            connection, transaction, backlog.Id, ItemStatus.Done, ItemResolution.Obsolete, actor, cancellationToken);

        var summary = items.Count == 0
            ? $"{run.Ref}/backlog closed empty"
            : $"carried {items.Count} open item(s) to {target}/: {string.Join(", ", moved)}";

        await EventAsync(
            connection,
            transaction,
            run.WorkspaceId,
            run.Id,
            actor,
            "carried",
            $"{run.Ref} closing: {summary}",
            new { target, items = moved },
            cancellationToken);

        return summary;
    }

    /// <summary>
    /// What <c>target:"backlog"</c> means: the project-level run of <paramref name="from"/>'s
    /// project. That is an open run named "backlog" under the project if one exists (the hand-made
    /// one live data already has), else the one this server made before, else a new one whose ref
    /// no item in the project uses, so it can never be mistaken for a run's own backlog phase.
    /// </summary>
    private static async Task<Carried> BacklogRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid from,
        CancellationToken cancellationToken)
    {
        const string projectSql =
            """
            SELECT a.id AS "Id", a.ref AS "Ref"
            FROM   item r
            JOIN   item a ON a.id = ANY (r.ancestors || r.id)
            WHERE  r.id = @from AND a.kind = 'project'
            ORDER  BY array_position(r.ancestors || r.id, a.id) DESC
            LIMIT  1;
            """;

        var project = await connection.QuerySingleAsync<Carried>(new CommandDefinition(
            projectSql, new { from }, transaction, cancellationToken: cancellationToken));

        const string existingSql =
            """
            SELECT id AS "Id", ref AS "Ref"
            FROM   item
            WHERE  parent_id = @project
              AND  kind = 'run'
              AND  id <> @from
              AND  status NOT IN ('done','cancelled','superseded')
              AND  (lower(ref) = 'backlog' OR title = @title)
            ORDER  BY (lower(ref) = 'backlog') DESC, seq
            LIMIT  1;
            """;

        var existing = await connection.QueryFirstOrDefaultAsync<Carried>(new CommandDefinition(
            existingSql,
            new { project = project.Id, from, title = BacklogRunTitle },
            transaction,
            cancellationToken: cancellationToken));

        if (existing is not null)
        {
            return existing;
        }

        const string takenSql =
            """
            SELECT EXISTS (
              SELECT 1 FROM item
              WHERE  (id = @project OR ancestors @> ARRAY[@project]::uuid[])
                AND  lower(ref) = lower(@ref));
            """;

        var reference = string.Empty;
        for (var n = 1; n < 500; n++)
        {
            reference = n switch
            {
                1 => "backlog",
                2 => $"{project.Ref}-backlog",
                _ => $"{project.Ref}-backlog{n - 1}",
            };
            var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                takenSql, new { project = project.Id, @ref = reference }, transaction, cancellationToken: cancellationToken));

            if (!taken)
            {
                break;
            }
        }

        var created = new NewRow(
            Guid.CreateVersion7(),
            workspaceId,
            project.Id,
            "run",
            reference,
            await NextSeqAsync(connection, transaction, project.Id, cancellationToken),
            BacklogRunTitle);

        await InsertItemAsync(connection, transaction, created, cancellationToken);

        // An empty key is the claim fallback (TreeLockAsync): a follow-up locks the tree it is
        // worked in — tree:, else the project's repo root — since a backlog owns no tree of its own.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO run_config (item_id, project_item_id, serialization_key)
            VALUES (@run, @project, '');
            """,
            new { run = created.Id, project = project.Id },
            transaction,
            cancellationToken: cancellationToken));

        return new Carried(created.Id, created.Ref);
    }
}

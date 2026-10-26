using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// A repository as the caller names it — <c>git remote get-url origin</c> and the parent of
/// <c>git rev-parse --path-format=absolute --git-common-dir</c>. An object rather than one string
/// because the two keys are independent: a repo with no remote still has a root.
/// </summary>
public sealed record RepoArg(
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("root")] string? Root = null)
{
    public IReadOnlyList<RepoKey> Keys() => RepoKeys.Of(Remote, Root);
}

public sealed partial class WorkWriter
{
    private sealed record BoundKey(string Key, string ProjectRef);

    /// <summary>
    /// Binds <paramref name="keys"/> to the project, or refuses naming the project that already
    /// holds one: one project per repository. A key the project already holds is left as it is.
    /// The caller returns the refusal without committing, so its transaction rolls back whole.
    /// <para>
    /// Insert first, then ask who holds a key that did not insert: the unique index waits out a
    /// concurrent binder, so the owner read after it sees that binder's committed row.
    /// </para>
    /// </summary>
    private static async Task<(IReadOnlyList<RepoKey> Added, string? Refusal)> BindReposAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        string projectRef,
        IReadOnlyList<RepoKey> keys,
        CancellationToken cancellationToken)
    {
        const string insert =
            """
            INSERT INTO project_repo (id, workspace_id, project_item_id, kind, key)
            VALUES (@id, @workspace, @project, @kind, @key)
            ON CONFLICT (workspace_id, key) DO NOTHING;
            """;

        const string owner =
            """
            SELECT r.key AS "Key", p.ref AS "ProjectRef"
            FROM   project_repo r JOIN item p ON p.id = r.project_item_id
            WHERE  r.workspace_id = @workspace AND r.key = @key AND r.project_item_id <> @project;
            """;

        var added = new List<RepoKey>();
        foreach (var key in keys)
        {
            var rows = await connection.ExecuteAsync(new CommandDefinition(
                insert,
                new { id = Guid.CreateVersion7(), workspace = workspaceId, project = projectId, kind = key.Kind, key = key.Key },
                transaction,
                cancellationToken: cancellationToken));

            if (rows > 0)
            {
                added.Add(key);
                continue;
            }

            var held = await connection.QueryFirstOrDefaultAsync<BoundKey>(new CommandDefinition(
                owner,
                new { workspace = workspaceId, key = key.Key, project = projectId },
                transaction,
                cancellationToken: cancellationToken));

            if (held is not null)
            {
                return ([], Refusal.Blocked(
                    $"{key.Kind} \"{key.Key}\" is already bound to project {held.ProjectRef}.",
                    $"this repo is project {held.ProjectRef} — one project per repository, so "
                    + $"{projectRef} cannot hold it too",
                    $"work_brief(scope:\"{held.ProjectRef}\") to work in it",
                    $"work_brief(repo:{RepoKeys.Literal(keys)}) — the same project, found from the repo"));
            }
        }

        return (added, null);
    }

    private static string NotAProject(string call, string reference, string kind)
        => Refusal.Blocked(
            $"{call} — repo: binds a project, and {reference} is a {kind}.",
            "a repository belongs to its project; runs and tasks inside it find it from there",
            "work_update(ref:\"<the project's ref>\", repo:{remote:\"…\", root:\"…\"})",
            "work_brief(repo:{remote:\"…\", root:\"…\"}) to find the project this repo is bound to");

    private static string RepoLine(IReadOnlyList<RepoKey> keys)
        => "repo      " + string.Join(", ", keys.Select(k => $"{k.Kind} {k.Key}"));
}

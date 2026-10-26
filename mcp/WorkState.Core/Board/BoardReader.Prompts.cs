using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Core.Board;

public sealed partial class BoardReader
{
    /// <summary>The prompt actions that apply to a scope (<see cref="PromptCatalogue"/>), in catalogue order.</summary>
    public async Task<IReadOnlyList<PromptAction>> PromptsAsync(
        ScopeResult.Found scope,
        CancellationToken cancellationToken = default)
        => PromptCatalogue.For(await PromptFactsAsync(scope, cancellationToken));

    public async Task<PromptFacts> PromptFactsAsync(ScopeResult.Found scope, CancellationToken cancellationToken = default)
    {
        var item = scope.Item;
        var callable = await CallableAsync(item, cancellationToken);

        return item.Kind switch
        {
            "project" => await ProjectPromptFactsAsync(scope, callable, cancellationToken),
            "run" => new PromptFacts(
                PromptScope.Run,
                callable,
                item.Kind,
                Glyph.Flat(item.Title),
                item.Status,
                DoneLeaves: (await queries.SubtreeAsync(item.Id, cancellationToken))
                    .Count(n => Leaf(n.Kind) && n.Status == ItemStatus.Done)),
            _ => new PromptFacts(PromptScope.Item, callable, item.Kind, Glyph.Flat(item.Title), item.Status),
        };
    }

    private async Task<PromptFacts> ProjectPromptFactsAsync(
        ScopeResult.Found scope,
        string callable,
        CancellationToken cancellationToken)
    {
        var project = scope.Item;
        var runs = await board.ProjectRunsAsync(project.Id, WorkWriter.BacklogRunTitle, cancellationToken);
        var backlogRun = runs.FirstOrDefault(r => !r.Closed && r.Backlog);
        var backlog = await board.ProjectBacklogAsync(project.Id, backlogRun?.Id, cancellationToken);

        // Finalize applies once no run but the backlog has an unclosed leaf under it — a run or phase
        // still open over closed work is what the finalize prompt closes — and never once archived.
        var work = runs.Where(r => !r.Backlog).ToList();
        var finalizable = work.Count > 0 && await board.ArchivedAtAsync(project.Id, cancellationToken) is null;
        foreach (var run in work.Where(r => !r.Closed))
        {
            finalizable = finalizable && !(await queries.SubtreeAsync(run.Id, cancellationToken))
                .Any(n => Leaf(n.Kind) && !PromptCatalogue.Closed(n.Status));
        }

        return new PromptFacts(
            PromptScope.Project,
            callable,
            project.Kind,
            Glyph.Flat(project.Title),
            project.Status,
            Unclosed: await UnclosedCountAsync(scope, cancellationToken),
            Backlog: backlog.Count,
            BacklogRef: backlogRun is null ? null : await CallableAsync(backlogRun.Scope, cancellationToken),
            Finalizable: finalizable);
    }

    /// <summary>The leaf kinds of doc 10's Rollup.</summary>
    private static bool Leaf(string kind)
        => kind is "task" or "followup" or "subtask" or "spike" or "question" or "incident";
}

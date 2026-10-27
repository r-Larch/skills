using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Core.Board;

public sealed partial class BoardReader
{
    /// <summary>
    /// assumption-reach. An assumption is not an item, so <c>work_get(refs:["A1"])</c> answered "no
    /// item has ref A1" and its text was readable only in <c>work_threads</c>. This is its detail view:
    /// everything it holds, whole.
    /// </summary>
    private static IEnumerable<string> AssumptionDetail(AssumptionDetailRow a)
    {
        var state = a.State.ToString().ToLowerInvariant();
        yield return $"{Glyph.Pad(a.Ref, Columns.Ref)}  {Glyph.Pad(Glyph.Kind("assumption"), Columns.Kind)}  "
                     + $"{Glyph.Pad(state, Columns.Status)}  {a.QualifiedRef}";

        foreach (var line in BriefRenderer.Wrap("  statement ", a.Statement, 12))
        {
            yield return line;
        }

        var evidence = a.Evidence.ToString().ToLowerInvariant();
        foreach (var line in BriefRenderer.Wrap(
                     "  evidence  ", a.EvidenceNote is { Length: > 0 } note ? $"{evidence} — {note}" : evidence, 12))
        {
            yield return line;
        }

        if (a.Reversal is { } reversal)
        {
            yield return $"  reversal  {reversal.ToString().ToLowerInvariant()}";
        }

        if (a.RevisitRef is { Length: > 0 } revisit)
        {
            yield return $"  revisit   when {revisit} closes (it is {Transitions.Name(a.RevisitStatus ?? ItemStatus.Open)})";
        }
        else if (a.RevisitAt is { } at)
        {
            yield return $"  revisit   at {at.UtcDateTime:yyyy-MM-dd}";
        }

        yield return $"  scope     {a.ScopeRef ?? "the workspace"}";

        if (a.State != AssumptionState.Open)
        {
            var when = a.ResolvedAt is { } resolved ? $" {resolved.UtcDateTime:yyyy-MM-dd}" : string.Empty;
            var why = a.ResolutionNote is { Length: > 0 } resolution ? $" — {resolution}" : string.Empty;
            foreach (var line in BriefRenderer.Wrap("  resolved  ", $"{state}{when}{why}", 12))
            {
                yield return line;
            }
        }

        if (a.SourcePath is { } path)
        {
            var source = a.SourceAnchor is { Length: > 0 } anchor ? $"{path}#{anchor}" : path;
            foreach (var line in BriefRenderer.Wrap(
                         "  imported  ",
                         $"from {source} by {a.ImportedBy}, {Glyph.Ago(Since(a.ImportedAt ?? a.CreatedAt))}",
                         12))
            {
                yield return line;
            }

            foreach (var line in BriefRenderer.Wrap("  why       ", a.Why, 12))
            {
                yield return line;
            }
        }
    }

    private static string AssumptionCallable(AssumptionDetailRow a) => a.Qualified ? a.QualifiedRef : a.Ref;

    /// <summary>An assumption on no item whose bare ref a run's assumption shares, renamed to the <c>workspace/</c> form that names it alone.</summary>
    private async Task<AssumptionDetailRow> CallableAssumptionAsync(
        Guid workspaceId,
        AssumptionDetailRow assumption,
        CancellationToken cancellationToken)
    {
        if (assumption.Qualified)
        {
            return assumption;
        }

        var form = await board.CallableAssumptionRefAsync(workspaceId, assumption.Ref, cancellationToken);
        return form == assumption.Ref ? assumption : assumption with { QualifiedRef = form, Qualified = true };
    }

    private static string AssumptionNext(AssumptionDetailRow a)
        => a.State == AssumptionState.Open
            ? $"Next: work_assume(ref:\"{AssumptionCallable(a)}\", resolve:\"confirmed\", evidence:\"…\") once it is "
              + "settled — or disproven, obsolete."
            : $"Next: nothing — {AssumptionCallable(a)} is {a.State.ToString().ToLowerInvariant()}.";
}

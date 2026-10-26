using WorkState.Core.Domain;

namespace WorkState.Core.Rendering;

public sealed record OpenRunLine(string Ref, string Title, int Done, int Total, string Next, IReadOnlyList<string> Held);

public sealed record ClosedRunLine(string Ref, string Title, string Outcome, DateTimeOffset? ClosedAt, string? Commit);

public sealed record BacklogLine(string Ref, string Kind, string Title);

/// <summary>
/// What <c>work_brief</c> renders for a project: the continue-here context of a repository. The
/// <c>*Total</c> counts are taken before capping, like <see cref="BriefModel"/>'s.
/// </summary>
public sealed record ProjectBriefModel(
    string ProjectRef,
    string Title,
    string? Repos,
    string? Goal,
    IReadOnlyList<OpenRunLine> OpenRuns,
    IReadOnlyList<ClosedRunLine> Recent,
    int ClosedTotal,
    string ClosedRoute,
    IReadOnlyList<BacklogLine> Backlog,
    int BacklogTotal,
    string BacklogRoute,
    IReadOnlyList<BindingLine> Binding,
    int BindingTotal,
    IReadOnlyList<string> Notes,
    int NoteTotal,
    int AddressedTotal,
    string? AddressedTo,
    VerifyLine? LastVerify,
    string Next,
    IReadOnlyList<AttentionLine> Attention);

/// <summary>
/// doc 06 §4a. Nobody works on a project, so its NEXT never transitions it: it names the run to
/// resume, or the call that starts one.
/// </summary>
public static class ProjectBriefRenderer
{
    private const int RowIndent = 4;

    public static string Render(ProjectBriefModel model)
    {
        var head = $"PROJECT {model.ProjectRef}  ·  {Glyph.Flat(model.Title)}";
        var lines = new List<string>
        {
            model.Repos is { Length: > 0 } repos ? $"{head}  ·  repo {repos}" : head,
        };

        if (model.Goal is { Length: > 0 })
        {
            lines.AddRange(BriefRenderer.Wrap("GOAL ", model.Goal, 5));
        }

        lines.Add(string.Empty);
        if (model.OpenRuns.Count == 0)
        {
            lines.Add("OPEN RUNS (0)");
        }
        else
        {
            lines.Add($"OPEN RUNS ({model.OpenRuns.Count})");
            foreach (var run in model.OpenRuns)
            {
                lines.AddRange(BriefRenderer.Wrap($"  {run.Ref} — ", run.Title, RowIndent));
                var held = run.Held.Count > 0 ? $"  ·  held: {string.Join(", ", run.Held)}" : string.Empty;
                lines.AddRange(BriefRenderer.Wrap("    ", $"{run.Done}/{run.Total} tasks done{held}", RowIndent));

                // Unwrapped like the run brief's NEXT: a call broken across lines cannot be copied.
                lines.Add($"    next  {Glyph.Flat(run.Next)}");
            }
        }

        if (model.ClosedTotal > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"RECENT ({model.Recent.Count} of {model.ClosedTotal} closed)");
            foreach (var run in model.Recent)
            {
                var when = run.ClosedAt is { } at ? $"  ·  {at.UtcDateTime:yyyy-MM-dd}" : string.Empty;
                var commit = run.Commit is { Length: > 0 } sha ? $"  ·  {sha}" : string.Empty;
                lines.AddRange(BriefRenderer.Wrap(
                    $"  {run.Ref} — ",
                    $"{run.Title} — {run.Outcome}{when}{commit}",
                    RowIndent));
            }

            var older = model.ClosedTotal - model.Recent.Count;
            if (older > 0)
            {
                lines.Add($"  · +{older} older — {model.ClosedRoute}");
            }
        }

        if (model.BacklogTotal > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"BACKLOG ({model.BacklogTotal} open)");
            foreach (var item in model.Backlog)
            {
                var label = $"  {Glyph.Pad(item.Ref, 6)} {Glyph.Pad(Glyph.Kind(item.Kind), 6)} ";
                lines.AddRange(BriefRenderer.Wrap(label, item.Title, label.Length));
            }

            var hidden = model.BacklogTotal - model.Backlog.Count;
            lines.Add(hidden > 0
                ? $"  · +{hidden} more — {model.BacklogRoute}"
                : $"  · all of them — {model.BacklogRoute}");
        }

        BriefRenderer.AddBinding(lines, model.Binding, model.BindingTotal);
        BriefRenderer.AddNotes(
            lines, model.Notes, model.NoteTotal, model.AddressedTotal, model.AddressedTo, model.ProjectRef, "project");
        BriefRenderer.AddLastVerify(lines, model.LastVerify);

        lines.Add(string.Empty);
        lines.Add($"NEXT   {Glyph.Flat(model.Next)}");

        return Attention.Append(string.Join("\n", lines), model.Attention);
    }

    public static string Outcome(ItemStatus status, string? resolution)
    {
        var closed = status.ToString().ToLowerInvariant();
        return resolution is { Length: > 0 } r ? $"{closed}/{r}" : closed;
    }
}

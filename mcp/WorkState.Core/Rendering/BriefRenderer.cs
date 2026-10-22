using System.Text;

namespace WorkState.Core.Rendering;

public sealed record BindingLine(string Ref, string Text, bool RedLine, string? Route = null);

public sealed record AssumptionLine(
    string Ref,
    string Statement,
    string Evidence,
    string? Reversal,
    string? RevisitRef);

public sealed record VerifyLine(
    string? Command,
    string ResultLine,
    string RunBy,
    TimeSpan Ago,
    bool Passed);

/// <summary>
/// Everything <c>work_brief</c> renders, already trimmed to what fits. The <c>*Total</c> counts are
/// the numbers <em>before</em> capping, so the header can say how much was left out and point at
/// the resource that holds the rest.
/// </summary>
public sealed record BriefModel(
    string RunRef,
    string? ProjectRef,
    string? Branch,
    string? Head,
    string? Goal,
    string Where,
    string Next,
    IReadOnlyList<BindingLine> Binding,
    int BindingTotal,
    IReadOnlyList<AssumptionLine> Assumptions,
    int AssumptionTotal,
    IReadOnlyList<string> Notes,
    int NoteTotal,
    int AddressedTotal,
    string? AddressedTo,
    VerifyLine? LastVerify,
    IReadOnlyList<AttentionLine> Attention,
    string? ScopeKind = null,
    string? ScopeRef = null);

public static class BriefRenderer
{
    private const int GoalIndent = 5;

    /// <summary>
    /// doc 06 §4. Target ≤35 lines, paid on every session start, and the reason every section is
    /// capped rather than complete: an unbounded brief is a brief nobody can afford to call.
    /// <paramref name="full"/> is the worker variant, which a controller pastes into a prompt
    /// whole: its GOAL and its rules wrap in full. The resume variant keeps both capped, because
    /// wrapped rules cost the sample fixture six lines over the 35-line target.
    /// </summary>
    public static string Render(BriefModel model, bool full = false)
    {
        var lines = new List<string>();
        var handle = model.ScopeRef is { Length: > 0 } scope ? scope : model.RunRef;

        // The scope labels itself by its own kind, and the run and the project are named as such.
        // A task printed as "RUN 3.1 · project phase-3" mislabels its own tree on the one surface an
        // agent learns the tree from (ACCEPTANCE A-3).
        var isRun = model.ScopeKind is not { Length: > 0 }
                    || model.ScopeKind.Equals("run", StringComparison.OrdinalIgnoreCase);

        var head = new StringBuilder(isRun
            ? $"RUN {model.RunRef}"
            : $"{model.ScopeKind!.ToUpperInvariant()} {model.ScopeRef ?? model.RunRef}");

        if (!isRun && model.RunRef is { Length: > 0 } && model.RunRef != model.ScopeRef)
        {
            head.Append($"  ·  run {model.RunRef}");
        }

        if (model.ProjectRef is { Length: > 0 })
        {
            head.Append($"  ·  project {model.ProjectRef}");
        }

        if (model.Branch is { Length: > 0 })
        {
            head.Append($"  ·  branch {model.Branch}");
            if (model.Head is { Length: > 0 })
            {
                head.Append($" @ {model.Head}");
            }
        }

        lines.Add(head.ToString());

        if (model.Goal is { Length: > 0 })
        {
            lines.AddRange(Wrap("GOAL ", model.Goal, GoalIndent, full ? null : 3));
        }

        lines.Add(string.Empty);
        lines.Add($"WHERE  {Glyph.Flat(model.Where)}");
        lines.Add($"NEXT   {Glyph.Flat(model.Next)}");

        if (model.BindingTotal > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"BINDING ({model.BindingTotal})");

            // The ref sits between the marker and the text, on the assumption row's four-wide
            // handle column: a rule an agent can read but not name is one it cannot question or
            // retire, and the marker still opens the line so the ! column stays scannable.
            //
            // The separator after the pad is not decoration. Pad(ref, 4) leaves nothing behind a
            // ref that is already four wide, and the corpus runs to C258 — so without it every
            // line from C100 up reads "C148Never connect to …". AssumptionRow has always appended
            // this space; copying the column without it is how the defect arrived.
            //
            // In the worker variant a rule wraps and never clips: the route is the end of the line,
            // and a red line with its route cut off forbids without saying what to do instead.
            foreach (var b in model.Binding)
            {
                var label = $"  {(b.RedLine ? "!" : "·")} {Glyph.Pad(Glyph.Flat(b.Ref), 4)} ";
                var text = b.Route is { Length: > 0 } ? $"{b.Text} — {b.Route}" : b.Text;
                lines.AddRange(full ? Wrap(label, text, label.Length) : [label + Glyph.Clip(text, 87)]);
            }

            AddOverflow(lines, model.BindingTotal - model.Binding.Count, $"read {WorkUris.Conventions}");
        }

        if (model.AssumptionTotal > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"OPEN ASSUMPTIONS ({model.AssumptionTotal})");
            lines.AddRange(model.Assumptions.Select(AssumptionRow));
            AddOverflow(
                lines,
                model.AssumptionTotal - model.Assumptions.Count,
                $"work_threads(scope:\"{handle}\", include:\"assumptions\")");
        }

        // A note addressed to an item and a note the run merely carries are different obligations,
        // and the header says which it is holding. "ADDRESSED TO YOU (140)" over 140 notes that name
        // no target is an honest count under a dishonest label, and the label is what an agent reads.
        if (model.NoteTotal > 0)
        {
            lines.Add(string.Empty);
            lines.Add(model.AddressedTo is { Length: > 0 }
                ? $"ADDRESSED TO YOU ({model.AddressedTotal})   — these render into "
                  + $"{model.AddressedTo}'s brief and are then marked consumed"
                : $"NOTES IN SCOPE ({model.NoteTotal})   — carried forward by this run; "
                  + "none names an item, so none is consumed");

            lines.AddRange(model.Notes.Select(n => $"  {Glyph.Clip(n, 94)}"));

            var hidden = model.NoteTotal - model.Notes.Count;
            if (hidden > 0)
            {
                lines.Add(
                    $"  · +{hidden} more unconsumed in scope — "
                    + $"work_threads(scope:\"{handle}\", include:\"notes\")");
            }
        }

        if (model.LastVerify is { } verify)
        {
            lines.Add(string.Empty);
            var command = verify.Command is { Length: > 0 } ? $"{Glyph.Clip(verify.Command, 46)} → " : string.Empty;
            lines.Add(
                $"LAST VERIFY  {command}{Glyph.Clip(verify.ResultLine, 34)}, "
                + $"by {verify.RunBy}, {Glyph.Age(verify.Ago)} ago");
        }

        return Attention.Append(string.Join("\n", lines), model.Attention);
    }

    /// <summary>
    /// The cold start. doc 04 §4 — an empty server explains itself through the same call as every
    /// other path, so there is no second mechanism to keep in step.
    /// </summary>
    public static string Onboarding()
        => """
           RUN —  ·  this server holds no work yet

           WHERE  Empty workspace. Nothing is tracked here.
           NEXT   work_create(items:[{kind:"project", ref:"<slug>", title:"<what the repo is>"}])

           WHAT GOES IN
             project  the repo.  run  one undertaking inside it.  phase  a stage of that run.
             task     one agent's job: a title, done-criteria, and a verification command.

           A GOOD verify_cmd CAN FAIL
             `dotnet test --filter FullyQualifiedName~NewThing` is a verification: it is red before
             the work and green after. `dotnet build` on an untouched tree is not — it was already
             green, so it checks nothing. Every task needs one, and work_create refuses a task without.

           THEN
             work_create the project, then its run, then decompose the run into phases and tasks.
             work_brief again once anything exists — it becomes the recovery payload for the session.
           """;

    private static string AssumptionRow(AssumptionLine line)
    {
        var builder = new StringBuilder("  ");
        builder.Append(Glyph.Pad(Glyph.Flat(line.Ref), 4)).Append(' ');
        builder.Append(Glyph.Pad(Glyph.Clip(line.Statement, 48), 48)).Append("  ");
        builder.Append(Glyph.Pad(line.Evidence, 9)).Append("  ");
        builder.Append(Glyph.Pad(line.Reversal ?? Glyph.None, 7));

        if (line.RevisitRef is { Length: > 0 })
        {
            builder.Append("  ").Append(Flag.Revisit(line.RevisitRef));
        }

        return builder.ToString().TrimEnd();
    }

    private static void AddOverflow(List<string> lines, int hidden, string route)
    {
        if (hidden > 0)
        {
            lines.Add($"  · +{hidden} more — {route}");
        }
    }

    internal static IEnumerable<string> Wrap(string label, string text, int indent, int? maxLines = null)
    {
        var words = Glyph.Flat(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pad = new string(' ', indent);
        var lines = new List<string>();
        var current = new StringBuilder(label);
        var prefix = label.Length;

        foreach (var word in words)
        {
            if (current.Length > prefix && current.Length + 1 + word.Length > Columns.Width)
            {
                lines.Add(current.ToString());
                if (lines.Count == maxLines)
                {
                    var last = lines[^1];
                    var cut = Math.Min(last.Length, Columns.Width - 1);
                    lines[^1] = last[..cut].TrimEnd() + Glyph.Ellipsis;
                    return lines;
                }

                current = new StringBuilder(pad);
                prefix = indent;
            }

            if (current.Length > prefix)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > prefix)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }
}

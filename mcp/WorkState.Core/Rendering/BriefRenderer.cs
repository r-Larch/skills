using System.Text;
using WorkState.Core.Domain;

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
    private const int StatementWidth = 48;

    /// <summary>
    /// doc 06 §4 and "What may be shortened": every text a section shows wraps whole; only the
    /// number of rules, assumptions and notes is capped, each cap naming the call that lists the rest.
    /// <paramref name="full"/> is the worker variant, which a controller pastes into a prompt.
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
            lines.AddRange(Wrap("GOAL ", model.Goal, GoalIndent));
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
            foreach (var b in model.Binding)
            {
                var label = $"  {(b.RedLine ? "!" : "·")} {Glyph.Pad(Glyph.Flat(b.Ref), 4)} ";
                var text = b.Route is { Length: > 0 } ? $"{b.Text} — {b.Route}" : b.Text;
                lines.AddRange(Wrap(label, text, label.Length));
            }

            AddOverflow(lines, model.BindingTotal - model.Binding.Count, $"read {WorkUris.Conventions}");
        }

        if (model.AssumptionTotal > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"OPEN ASSUMPTIONS ({model.AssumptionTotal})");
            lines.AddRange(model.Assumptions.SelectMany(AssumptionRows));
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

            lines.AddRange(model.Notes.SelectMany(n => Wrap("  ", n, 2)));

            var hidden = model.NoteTotal - model.Notes.Count;
            if (hidden > 0)
            {
                // work_threads lists only addressed notes; a carried note is printed whole in history.
                lines.Add(model.AddressedTo is { Length: > 0 }
                    ? $"  · +{hidden} more unconsumed in scope — work_threads(scope:\"{handle}\", include:\"notes\")"
                    : $"  · +{hidden} more carried in scope — work_history() prints each note whole");
            }
        }

        if (model.LastVerify is { } verify)
        {
            lines.Add(string.Empty);
            var command = verify.Command is { Length: > 0 } ? $"{verify.Command} → " : string.Empty;
            lines.AddRange(Wrap(
                "LAST VERIFY  ",
                $"{command}{verify.ResultLine}, by {verify.RunBy}, {Glyph.Age(verify.Ago)} ago",
                13));
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

    /// <summary>How many existing projects the unbound-repo onboarding names before it routes to a search.</summary>
    private const int UnboundProjectCap = 8;

    /// <summary>
    /// <c>work_brief(repo:…)</c> for a repository no project holds. The routes carry the caller's own
    /// keys, normalised, so the call that binds it is the one printed here — never a guess at which
    /// project it belongs to.
    /// </summary>
    public static string Unbound(IReadOnlyList<RepoKey> keys, IReadOnlyList<string> projects)
    {
        var repo = RepoKeys.Literal(keys);
        var lines = new List<string>
        {
            "RUN —  ·  this repo is not bound to a project",
            string.Empty,
        };

        lines.AddRange(keys.Select((k, i) => $"{(i == 0 ? "REPO   " : "       ")}{Glyph.Pad(k.Kind, 6)} {k.Key}"));
        lines.Add(string.Empty);
        lines.AddRange(Wrap(
            "WHERE  ",
            "No project holds this repository. One project per repository: once bound, every later "
            + "session here, in any worktree of it, opens that project from work_brief(repo:…).",
            7));
        lines.Add($"NEXT   work_create(items:[{{kind:\"project\", ref:\"<slug>\", title:\"<what the repo is>\", repo:{repo}}}])");

        if (projects.Count > 0)
        {
            var shown = projects.Take(UnboundProjectCap).ToList();
            var more = projects.Count > shown.Count
                ? $" +{projects.Count - shown.Count} more — work_search(q:\"…\")"
                : string.Empty;

            lines.Add($"OR     work_update(ref:\"<project>\", repo:{repo}) when it belongs to one that exists:");
            lines.AddRange(Wrap("         ", string.Join(", ", shown) + more, 9));
            return string.Join("\n", lines);
        }

        var onboarding = Onboarding();
        lines.Add(string.Empty);
        lines.Add(onboarding[onboarding.IndexOf("WHAT GOES IN", StringComparison.Ordinal)..]);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// One aligned row while the statement fits its column. A longer statement wraps whole across the
    /// line and its evidence and reversal move to a line of their own beneath it, so the statement
    /// reads as one unbroken sentence.
    /// </summary>
    private static IEnumerable<string> AssumptionRows(AssumptionLine line)
    {
        var head = $"  {Glyph.Pad(Glyph.Flat(line.Ref), 4)} ";
        var tail = new StringBuilder();
        tail.Append(Glyph.Pad(line.Evidence, 9)).Append("  ");
        tail.Append(Glyph.Pad(line.Reversal ?? Glyph.None, 7));

        if (line.RevisitRef is { Length: > 0 })
        {
            tail.Append("  ").Append(Flag.Revisit(line.RevisitRef));
        }

        var statement = Glyph.Flat(line.Statement);
        if (statement.Length <= StatementWidth)
        {
            return [$"{head}{Glyph.Pad(statement, StatementWidth)}  {tail}".TrimEnd()];
        }

        return [.. Wrap(head, statement, head.Length), $"{new string(' ', head.Length)}{tail}".TrimEnd()];
    }

    private static void AddOverflow(List<string> lines, int hidden, string route)
    {
        if (hidden > 0)
        {
            lines.Add($"  · +{hidden} more — {route}");
        }
    }

    public static IReadOnlyList<string> Wrap(string label, string? text, int indent, int width = Columns.Width)
    {
        var words = Glyph.Flat(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pad = new string(' ', indent);
        var lines = new List<string>();
        var current = new StringBuilder(label);
        var prefix = label.Length;

        foreach (var word in words)
        {
            if (current.Length > prefix && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
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

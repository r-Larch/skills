using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Core.Board;

public sealed partial class BoardReader
{
    private static readonly string[] AllThreads =
        ["assumptions", "findings", "followups", "notes", "stale", "deps"];

    /// <summary>doc 01 §3.1 — the ready frontier, with the verification each row will be judged by.</summary>
    public async Task<string> NextAsync(
        ScopeResult.Found scope,
        int limit,
        string? kind,
        CancellationToken cancellationToken = default)
    {
        var frontier = await queries.ReadyFrontierAsync(scope.Item.Id, cancellationToken);
        var filtered = kind is { Length: > 0 }
            ? frontier.Where(f => f.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. frontier];

        if (filtered.Count == 0)
        {
            return $"NEXT  nothing is ready under {scope.Item.Ref}.\n"
                 + $"      work_threads(scope:\"{scope.Item.Ref}\", include:\"deps\") shows what is waiting, "
                 + $"work_tree(scope:\"{scope.Item.Ref}\") the shape.";
        }

        var shown = filtered.Take(limit).ToList();
        var facts = await FactsAsync(shown.Select(f => f.Id), cancellationToken);

        var lines = new List<string>
        {
            $"NEXT  {shown.Count} of {filtered.Count} ready under {scope.Item.Ref}",
            string.Empty,
        };

        foreach (var row in shown)
        {
            facts.TryGetValue(row.Id, out var fact);
            lines.Add(BoardRenderer.Row(new BoardItem(
                row.Ref,
                row.Kind,
                ItemStatus.Ready,
                row.Title,
                Evidence: EvidenceOf(fact),
                Flags: fact?.ReviewRequired == true ? [Flag.ReviewRequired] : null)));

            if (row.VerifyCmd is { Length: > 0 })
            {
                lines.AddRange(BriefRenderer.Wrap("      verify  ", row.VerifyCmd, 14));
            }
            else
            {
                lines.Add($"      verify  none recorded — work_update(ref:\"{row.Ref}\", verify_cmd:\"…\") before closing");
            }
        }

        lines.Add(string.Empty);
        lines.Add($"Next: work_get(refs:[\"{shown[0].Ref}\"]) for the full brief, "
                + $"then work_status(ref:\"{shown[0].Ref}\", to:\"in_progress\").");

        return string.Join("\n", lines);
    }

    /// <summary>doc 06 §3 — the shape, indented, with a rollup on every container.</summary>
    public async Task<string> TreeAsync(
        ScopeResult.Found scope,
        int depth,
        IReadOnlyCollection<string> statuses,
        bool includeCounts,
        CancellationToken cancellationToken = default)
    {
        var keep = new HashSet<ItemStatus>();
        foreach (var name in statuses)
        {
            if (!Transitions.TryParseStatus(name, out var parsed))
            {
                return Refusal.Blocked(
                    $"work_tree(status:\"{string.Join(",", statuses)}\") on {scope.Item.Ref}.",
                    $"\"{name}\" is not a status",
                    $"work_tree(scope:\"{scope.Item.Ref}\", status:\"…\") with any of: "
                    + string.Join(", ", Enum.GetValues<ItemStatus>().Select(Transitions.Name)));
            }

            keep.Add(parsed);
        }

        var subtree = await queries.SubtreeAsync(scope.Item.Id, cancellationToken);
        if (subtree.Count == 0)
        {
            return $"TREE  {scope.Item.Ref} is empty.";
        }

        var root = subtree.First(n => n.Id == scope.Item.Id);
        var facts = await FactsAsync(subtree.Select(n => n.Id), cancellationToken);
        var blocked = (await board.BlockedByAsync([scope.Item.Id], cancellationToken))
            .ToLookup(b => b.ItemId, b => b.BlockedByRef);

        var nodes = subtree
            .Where(n => n.Depth - root.Depth <= depth)
            .Where(n => keep.Count == 0 || n.Id == root.Id || keep.Contains(n.Status))
            .ToList();

        var lines = new List<string>();
        foreach (var node in nodes)
        {
            facts.TryGetValue(node.Id, out var fact);
            var total = node.OpenCount + node.DoingCount + node.BlockedCount
                      + node.ReviewCount + node.DoneCount;
            var rollup = includeCounts && total > 0 ? $"{node.DoneCount}/{total} done" : null;

            lines.Add(BoardRenderer.TreeRow(new TreeItem(
                new BoardItem(
                    node.Ref,
                    node.Kind,
                    node.Status,
                    node.Title,
                    DoingFor: node.Status == ItemStatus.InProgress ? Ago(fact?.StartedAt) : null,
                    Evidence: EvidenceOf(fact),
                    Flags: FlagsFor(node.Id, node.Status, fact, blocked)),
                node.Depth - root.Depth,
                rollup)));
        }

        lines.Add(string.Empty);
        lines.Add($"Next: work_next(scope:\"{scope.Item.Ref}\") to act, "
                + $"or work_threads(scope:\"{scope.Item.Ref}\") for what is unclosed.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// doc 01 §3.1. The body is never inlined — it is named as a <c>work://</c> URI, because a
    /// resource link renders as a bare URI in this client and only a named follow-up call retrieves it.
    /// </summary>
    public async Task<string> GetAsync(
        ScopeResult.Found home,
        IReadOnlyList<string> references,
        IReadOnlyCollection<string> include,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = home.WorkspaceId;
        var resolved = new List<ScopeRef>();
        var assumptions = new List<AssumptionDetailRow>();
        var problems = new List<string>();

        foreach (var reference in references)
        {
            var matches = await board.ResolveAsync(workspaceId, reference.Trim(), cancellationToken);
            switch (matches.Count)
            {
                case 1:
                    resolved.Add(matches[0]);
                    break;
                case > 1:
                    // ACCEPTANCE A-1. Every candidate, the caller's own run first — a single
                    // confidently-named wrong run is worse than naming none, because a careful
                    // agent acts on a callable line and this one is callable.
                    // ws/13: only forms that each resolve to one match alone.
                    var candidates = await board.UniqueRefsAsync(
                        workspaceId, Prefer(matches, home.Item).Take(5), cancellationToken);
                    problems.Add(candidates.Count == 0
                        ? $"✗ \"{reference}\" exists in {matches.Count} runs and no ref names only one "
                          + "→ work_tree() to see where each sits"
                        : $"✗ \"{reference}\" exists in {matches.Count} runs — qualify it "
                          + $"→ work_get(refs:[{string.Join(", ", candidates.Select(c => $"\"{c}\""))}])");
                    break;
                default:
                    var assumed = await board.AssumptionByRefAsync(workspaceId, reference.Trim(), cancellationToken);
                    if (assumed.Count == 1)
                    {
                        assumptions.Add(assumed[0]);
                        break;
                    }

                    var forms = assumed.Where(a => a.Qualified).Take(5).Select(a => $"\"{a.QualifiedRef}\"").ToList();
                    problems.Add(assumed.Count == 0
                        ? $"✗ no item or assumption has ref \"{reference}\" "
                          + $"→ work_search(q:\"{reference}\") or work_tree()"
                        : forms.Count == 0
                            ? $"✗ \"{reference}\" names {assumed.Count} assumptions and no ref names only one "
                              + "→ work_threads(include:\"assumptions\") lists them"
                            : $"✗ \"{reference}\" names {assumed.Count} assumptions across runs — qualify it "
                              + $"→ work_get(refs:[{string.Join(", ", forms)}])");
                    break;
            }
        }

        if (resolved.Count == 0)
        {
            if (assumptions.Count > 0)
            {
                return string.Join("\n", [.. problems, .. assumptions.SelectMany(AssumptionDetail), string.Empty, AssumptionNext(assumptions[0])]);
            }

            return problems.Count == 0
                ? "work_get needs at least one ref → work_next() or work_tree() to find one."
                : string.Join("\n", problems);
        }

        // ws/10: everything below is keyed by item id. Two runs each hold a "2", and keying these
        // on the bare ref rendered both items' criteria and evidence under each of them.
        var ids = resolved.Select(r => r.Id).Distinct().ToList();
        var facts = await FactsAsync(ids, cancellationToken);
        var criteria = (await board.CriteriaAsync(ids, cancellationToken)).ToLookup(c => c.ItemId);
        var links = include.Contains("deps")
            ? await board.LinksAsync(ids, cancellationToken)
            : [];
        var verifications = (await board.VerificationsAsync(ids, cancellationToken)).ToLookup(v => v.ItemId);
        var reports = (await board.ReportsAsync(ids, cancellationToken)).ToLookup(r => r.ItemId);
        var blocked = (await board.BlockedByAsync(ids, cancellationToken)).ToLookup(b => b.ItemId, b => b.BlockedByRef);
        var projects = resolved.Where(r => r.Kind == "project").Select(r => r.Id).Distinct().ToList();
        var repos = (projects.Count == 0 ? [] : await board.ProjectReposAsync(projects, cancellationToken))
            .ToLookup(r => r.ProjectId);
        var imports = (await board.ImportsAsync(ids, cancellationToken)).ToLookup(r => r.ItemId);

        var lines = new List<string>(problems);

        foreach (var item in resolved)
        {
            facts.TryGetValue(item.Id, out var fact);
            var callable = await CallableAsync(item, fact, cancellationToken);

            lines.Add(BoardRenderer.Row(new BoardItem(
                item.Ref,
                item.Kind,
                item.Status,
                item.Title,
                DoingFor: item.Status == ItemStatus.InProgress ? Ago(fact?.StartedAt) : null,
                Evidence: EvidenceOf(fact),
                Flags: FlagsFor(item.Id, item.Status, fact, blocked))));

            // The head row is the one place this view shortens; a title it cut is printed whole here.
            if (Glyph.Flat(item.Title).Length > Columns.Title)
            {
                lines.AddRange(BriefRenderer.Wrap("  title     ", item.Title, 12));
            }

            if (item.Intent is { Length: > 0 })
            {
                lines.AddRange(BriefRenderer.Wrap("  intent    ", item.Intent, 12));
            }

            foreach (var repo in repos[item.Id])
            {
                lines.Add($"  repo      {Glyph.Pad(repo.Kind, 6)} {repo.Key}");
            }

            foreach (var imported in imports[item.Id])
            {
                lines.AddRange(BriefRenderer.Wrap(
                    "  imported  ",
                    $"from {imported.Source} by {imported.ImportedBy}, {Glyph.Ago(Since(imported.UpdatedAt))}",
                    12));
                lines.AddRange(BriefRenderer.Wrap("  why       ", imported.Why, 12));
            }

            foreach (var criterion in criteria[item.Id])
            {
                var label = $"  {(criterion.Checked ? "✓" : "✗")} c{criterion.Seq}     ";
                lines.AddRange(BriefRenderer.Wrap(label, criterion.Text, label.Length));
            }

            foreach (var verification in verifications[item.Id])
            {
                lines.AddRange(BriefRenderer.Wrap(
                    "  evidence  ",
                    $"{(verification.Passed ? "✓" : "✗")} {verification.ResultLine} "
                    + $"by {verification.RunBy.ToString().ToLowerInvariant()}, "
                    + Glyph.Ago(Since(verification.At)),
                    12));
            }

            // work_history lists a report as one line; this is the only view that prints its body.
            foreach (var report in reports[item.Id])
            {
                lines.AddRange(BriefRenderer.Wrap(
                    "  report    ",
                    $"attempt {report.N} {report.Status} by {report.AgentName ?? Glyph.None} — {report.Raw}",
                    12));
            }

            foreach (var link in links.Where(l => l.FromId == item.Id || l.ToId == item.Id))
            {
                var arrow = link.FromId == item.Id
                    ? $"{link.Type.ToString().ToLowerInvariant()} → {link.ToRef}"
                    : $"← {link.FromRef} {link.Type.ToString().ToLowerInvariant()} this ({link.FromStatus.ToString().ToLowerInvariant()})";
                lines.Add($"  link      {arrow}");
            }

            // doc 01 §8: templates are undiscoverable, so a tool that names an item prints the whole
            // URI for both projections rather than leaving the model to compose the second one.
            lines.Add($"  uri       {WorkUris.Item(callable)}");
            lines.Add($"  brief     {WorkUris.ItemBrief(callable)} — read it with ReadMcpResourceTool");
        }

        lines.AddRange(assumptions.SelectMany(AssumptionDetail));

        facts.TryGetValue(resolved[0].Id, out var head);
        var next = await CallableAsync(resolved[0], head, cancellationToken);

        lines.Add(string.Empty);
        lines.Add(Transitions.IsTerminal(resolved[0].Status)
            ? $"Next: work_next() for what is ready — {next} is already {Transitions.Name(resolved[0].Status)}; "
              + $"work_create(items:[{{kind:\"followup\", parent:\"{next}\", title:\"…\", verify_cmd:\"…\", criteria:[…]}}]) "
              + "to carry more work forward."
            : $"Next: work_status(ref:\"{next}\", to:\"in_progress\") to start it, "
              + $"or work_brief(scope:\"{next}\", for:\"worker\") to hand it over.");

        return string.Join("\n", lines);
    }

    public async Task<string> SearchAsync(
        Guid workspaceId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var hits = await queries.SearchAsync(workspaceId, query, limit, cancellationToken);
        if (hits.Count == 0)
        {
            return $"SEARCH \"{Glyph.Flat(query)}\" — no hits.\n"
                 + "      Try fewer words, or work_tree() to see what is here.";
        }

        var lines = new List<string> { $"SEARCH \"{Glyph.Flat(query)}\" — {hits.Count} hits", string.Empty };

        lines.AddRange(hits.Select(hit =>
            $"{Glyph.Pad(Glyph.Flat(hit.Ref), 18)}  "
            + $"{Glyph.Pad(hit.Source == "item" ? "item" : "doc", 5)}  "
            + $"{Glyph.Pad(Glyph.Row(hit.Title, 30), 30)}  "
            + Glyph.Row(hit.Snippet, 40)));

        lines.Add(string.Empty);
        lines.Add(hits[0].Source == "item"
            ? $"Next: work_get(refs:[\"{hits[0].Ref}\"]) on a hit."
            : $"Next: read {WorkUris.Doc(hits[0].Ref.Split('#')[0])} for a doc hit's whole text, "
              + "work_get(refs:[…]) for an item hit.");
        return string.Join("\n", lines);
    }

    /// <summary>doc 01 §3.1 — everything unclosed that is not a task, each line with its own call.</summary>
    public async Task<string> ThreadsAsync(
        ScopeResult.Found scope,
        IReadOnlyCollection<string> include,
        CancellationToken cancellationToken = default)
    {
        var wanted = include.Count == 0 ? AllThreads : include;
        var lines = new List<string>();
        // A wrapped row spans several lines, so the header counts rows, not lines.
        var rows = 0;

        if (wanted.Contains("assumptions"))
        {
            var assumptions = await board.AssumptionsAsync(scope.WorkspaceId, scope.Item.Id, true, cancellationToken);
            rows += assumptions.Count;
            // An assumption has no detail view, so this is where its statement is read whole.
            lines.AddRange(assumptions.SelectMany(a => WrappedRow(
                $"{Glyph.Pad(a.Ref, 6)}  {Glyph.Pad("assum", 6)}  ",
                a.Statement,
                $"{Glyph.Pad(a.Evidence.ToString().ToLowerInvariant(), 9)}  "
                + $"— work_assume(ref:\"{a.Ref}\", resolve:\"confirmed\", evidence:\"…\")")));
        }

        var threads = await queries.OpenThreadsAsync(scope.WorkspaceId, scope.Item.Id, cancellationToken);

        if (wanted.Contains("findings"))
        {
            rows += threads.Count(t => t.Type == "finding");
            lines.AddRange(threads.Where(t => t.Type == "finding").Select(t =>
                $"{Glyph.Pad(t.Ref, 6)}  {Glyph.Pad("find", 6)}  "
                + $"{Glyph.Pad(Glyph.Row(t.Text, 46), 46)}  "
                + $"— work_status(ref:\"{t.Ref}\", to:\"done\")"));
        }

        if (wanted.Contains("followups"))
        {
            rows += threads.Count(t => t.Type == "followup");
            lines.AddRange(threads.Where(t => t.Type == "followup").Select(t =>
                $"{Glyph.Pad(t.Ref, 6)}  {Glyph.Pad("foll", 6)}  "
                + $"{Glyph.Pad(Glyph.Row(t.Text, 46), 46)}  "
                + $"— work_status(ref:\"{t.Ref}\", to:\"in_progress\")"));
        }

        if (wanted.Contains("notes"))
        {
            // Only the worker brief of its addressee consumes a note (Housekeeping.ConsumeNotesAsync),
            // so a note with no addressee is a record that never closes — not a thread.
            var notes = await board.UnconsumedNotesAsync(scope.WorkspaceId, scope.Item.Id, cancellationToken);
            // A note has no detail view and its brief consumes it, so its body is printed whole here.
            foreach (var n in notes.Where(n => n.ToItemId is not null))
            {
                rows++;
                lines.AddRange(WrappedRow(
                    $"{Glyph.Pad(n.ToRef!, 6)}  {Glyph.Pad("note", 6)}  ",
                    n.Body,
                    $"— work_brief(scope:\"{await AddresseeRefAsync(scope.WorkspaceId, n, cancellationToken)}\", "
                    + "for:\"worker\") consumes it"));
            }
        }

        if (wanted.Contains("deps"))
        {
            var deps = await queries.UnclosedDependenciesAsync(scope.Item.Id, cancellationToken);
            rows += deps.Count;
            lines.AddRange(deps.Select(d =>
                $"{Glyph.Pad(d.Waiting, 6)}  {Glyph.Pad("dep", 6)}  "
                + $"{Glyph.Pad($"waiting on {d.On} ({d.Status.ToString().ToLowerInvariant()})", 46)}  "
                + $"— work_status(ref:\"{d.On}\", to:\"done\") or work_link to re-point it"));
        }

        if (wanted.Contains("stale"))
        {
            var stale = await board.StaleInProgressAsync(scope.Item.Id, StaleHours, cancellationToken);
            rows += stale.Count;
            lines.AddRange(stale.Select(s =>
                $"{Glyph.Pad(s.Ref, 6)}  {Glyph.Pad("stale", 6)}  "
                + $"{Glyph.Pad($"in_progress {Glyph.Age(Since(s.StartedAt))}, no live claim", 46)}  "
                + $"— work_status(ref:\"{s.Ref}\", to:\"in_progress\") to retake it"));
        }

        if (lines.Count == 0)
        {
            return $"THREADS  nothing unclosed under {scope.Item.Ref}.\n"
                 + $"         work_next(scope:\"{scope.Item.Ref}\") for what to do instead.";
        }

        lines.Insert(0, string.Empty);
        lines.Insert(0, $"THREADS  {rows} unclosed under {scope.Item.Ref}");
        return string.Join("\n", lines);
    }

    /// <summary>doc 01 §5 — the mechanical half of the plan audit; every row carries its fix.</summary>
    public async Task<string> AuditAsync(
        ScopeResult.Found scope,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<AuditRow>();
        rows.AddRange(await board.ForcedTransitionsAsync(scope.Item.Id, cancellationToken));
        rows.AddRange(await board.NeverRedAsync(scope.Item.Id, cancellationToken));
        rows.AddRange(await board.SupersededStillReferencedAsync(scope.Item.Id, cancellationToken));
        rows.AddRange(await board.UncoveredRiskAsync(scope.Item.Id, cancellationToken));
        rows.AddRange(await board.RouteLessConstraintsAsync(scope.WorkspaceId, scope.Item.Id, cancellationToken));

        foreach (var unverified in await board.DoneUnverifiedAsync(scope.Item.Id, cancellationToken))
        {
            rows.Add(new AuditRow(
                "done-unverified",
                unverified.Ref,
                "closed with no passing orchestrator verification",
                $"work_verify(ref:\"{unverified.Ref}\", result_line:\"…\", run_by:\"orchestrator\")"));
        }

        foreach (var dep in await queries.UnclosedDependenciesAsync(scope.Item.Id, cancellationToken))
        {
            rows.Add(new AuditRow(
                dep.OrderedCorrectly ? "dep-unclosed" : "dep-inverted",
                dep.Waiting,
                $"waits on {dep.On} ({dep.Status.ToString().ToLowerInvariant()})"
                + (dep.OrderedCorrectly ? string.Empty : ", which is sequenced after it"),
                $"work_move(ref:\"{dep.On}\", before:\"{dep.Waiting}\") or work_link to re-point it"));
        }

        var lines = new List<string>
        {
            $"AUDIT  {scope.Item.Ref} — {rows.Count} mechanical finding(s)",
            "       Checks #3 (duplicate refs) and #2 (dangling deps) cannot fail here: "
            + "the schema refuses them.",
            "       #4 load-bearing facts and #8 shape need an agent — spend the budget there.",
            string.Empty,
        };

        if (rows.Count == 0)
        {
            lines.Add("  nothing mechanical to report.");
        }

        foreach (var group in rows.GroupBy(r => r.Check))
        {
            lines.Add($"{group.Key} ({group.Count()})");
            foreach (var row in group)
            {
                var label = $"  ✗ {Glyph.Pad(Glyph.Flat(row.Ref), 12)} ";
                lines.AddRange(BriefRenderer.Wrap(label, row.Problem, label.Length));
                lines.AddRange(BriefRenderer.Wrap("      → ", row.Route, 8));
            }
        }

        lines.Add(string.Empty);
        lines.Add("Next: work_update / work_link to fix what this found; "
                + "work_note(kind:\"decision\", body:\"…\") for what you are deliberately not fixing.");

        return string.Join("\n", lines);
    }

    public async Task<string> HistoryAsync(
        Guid workspaceId,
        ScopeResult.Found? scope,
        long sinceSeq,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var events = await board.EventsAsync(
            workspaceId,
            scope?.Item.Id,
            sinceSeq,
            limit,
            cancellationToken: cancellationToken);
        if (events.Count == 0)
        {
            return $"HISTORY  no events after seq {sinceSeq}.";
        }

        var lines = new List<string>
        {
            $"HISTORY  {events.Count} events, newest first (cursor: since_seq:{events[0].Seq})",
            string.Empty,
        };

        // An event has no detail view, so its summary wraps whole under its row.
        foreach (var e in events)
        {
            var label = $"{Glyph.Pad(e.Seq.ToString(), 7)}  "
                        + $"{Glyph.Pad(Glyph.Flat(e.ItemRef ?? Glyph.None), 6)}  "
                        + $"{Glyph.Pad(Glyph.Flat(e.Kind), 18)}  "
                        + $"{Glyph.Pad(Glyph.Flat(e.Actor), 18)}  ";
            var row = BriefRenderer.Wrap(label, e.Summary, 9);
            lines.AddRange(row.Count > 0 ? row : [label.TrimEnd()]);
        }

        lines.Add(string.Empty);
        lines.Add($"Next: work_get(refs:[\"{events.First(e => e.ItemRef is not null).ItemRef}\"]) "
                + "on anything surprising.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// doc 02 §8 — the workspace rules plus everything scoped to this item's ancestors, red lines
    /// first. A rule with no <c>route</c> is printed as such: a prohibition the agent cannot obey
    /// is a trap, and naming it is cheaper than discovering it mid-task.
    /// </summary>
    public async Task<string> ConventionsAsync(
        ScopeResult.Found scope,
        CancellationToken cancellationToken = default)
    {
        var rules = await board.ConstraintsAsync(scope.WorkspaceId, scope.Item.Id, cancellationToken);
        if (rules.Count == 0)
        {
            return $"CONVENTIONS  no active constraints in scope for {scope.Item.Ref}.";
        }

        var lines = new List<string> { $"CONVENTIONS  {rules.Count} active in scope for {scope.Item.Ref}", string.Empty };

        foreach (var rule in rules)
        {
            var label = $"{(rule.Kind == ConstraintKind.RedLine ? "!" : "·")} {Glyph.Pad(Glyph.Flat(rule.Ref), 4)}";
            lines.AddRange(BriefRenderer.Wrap(label, rule.Text, label.Length));
            if (rule.Route is { Length: > 0 })
            {
                lines.AddRange(BriefRenderer.Wrap("    → ", rule.Route, 6));
            }
        }

        return string.Join("\n", lines);
    }

    private async Task<Dictionary<Guid, ItemFacts>> FactsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        var rows = await board.FactsAsync([.. ids], cancellationToken);
        return rows.ToDictionary(r => r.Id);
    }

    /// <summary>
    /// The ref to print where a model will call or read it back: bare while it is unique in the
    /// workspace, qualified once another run holds the same one — a bare <c>work://item/2</c> over
    /// two runs is a refusal waiting to happen (ws/10).
    /// </summary>
    private static string Callable(string reference, ItemFacts? facts)
        => facts is { RefShared: true } ? facts.QualifiedRef : reference;

    /// <summary>
    /// <see cref="Callable"/> for an item that may be a run. A run numbers its own refs, so its
    /// <c>QualifiedRef</c> is its bare ref; the form printed is one checked to resolve to it alone
    /// (<c>autowork/backlog</c>), which is what the ambiguous-ref refusal suggests too (ws/13).
    /// </summary>
    private Task<string> CallableAsync(ScopeRef item, ItemFacts? facts, CancellationToken cancellationToken)
        => board.CallableRefAsync(item, facts is { RefShared: true }, cancellationToken);

    private static string EvidenceOf(ItemFacts? facts) => facts switch
    {
        null => Glyph.None,
        { CriteriaTotal: > 0 } f => Glyph.Evidence(f.CriteriaChecked, f.CriteriaTotal, f.CriteriaChecked < f.CriteriaTotal),
        { VerificationsPassed: > 0 } => "✓",
        _ => Glyph.None,
    };

    /// <summary>doc 06 §2 — absent means normal, so only an exception earns a marker.</summary>
    private static IReadOnlyList<string>? FlagsFor(
        Guid itemId,
        ItemStatus status,
        ItemFacts? facts,
        ILookup<Guid, string> blockedBy)
    {
        var flags = new List<string>();

        // doc 11 — who holds it, by session, so two controllers on one token are told apart.
        if (status == ItemStatus.InProgress && facts?.HeldBy is { Length: > 0 } holder)
        {
            flags.Add($"held by {holder}");
        }

        var blockers = blockedBy[itemId].ToList();
        if (blockers.Count > 0)
        {
            flags.Add(Flag.BlockedBy(string.Join(" ", blockers.Take(2))));
        }

        if (facts is { RequiresVerify: true, OrchestratorVerifications: 0, Moved: false }
            && status is ItemStatus.Done or ItemStatus.Review)
        {
            flags.Add(Flag.NoVerify);
        }

        if (facts is { ReviewRequired: true } && status is not (ItemStatus.Done or ItemStatus.Cancelled))
        {
            flags.Add(Flag.ReviewRequired);
        }

        if (facts is { Severity: "blocker" }
            && status is not (ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded))
        {
            flags.Add(Flag.Blocker(facts.ParentRef ?? "this run"));
        }

        if (facts is { OpenChildren: > 0 } && status is ItemStatus.Done or ItemStatus.Review)
        {
            flags.Add(Flag.OpenChildren(facts.OpenChildren));
        }

        if (facts?.Commit is { Length: > 0 } commit && status == ItemStatus.Done)
        {
            flags.Add(commit.Length > 7 ? commit[..7] : commit);
        }

        return flags.Count == 0 ? null : flags;
    }

    private static TimeSpan? Ago(DateTimeOffset? at) => at is { } value ? Since(value) : null;

    private const int ThreadTextWidth = 46;

    /// <summary>
    /// A threads row whose text is the only full copy anywhere. While the text fits its column the
    /// row is the usual one line; a longer text wraps whole and the tail (evidence, route) moves to a
    /// line of its own beneath it, so neither the text nor the call is broken up.
    /// </summary>
    private static IEnumerable<string> WrappedRow(string head, string? text, string tail)
    {
        var flat = Glyph.Flat(text);
        if (flat.Length <= ThreadTextWidth)
        {
            return [$"{head}{Glyph.Pad(flat, ThreadTextWidth)}  {tail}"];
        }

        return [.. BriefRenderer.Wrap(head, flat, head.Length), $"{new string(' ', head.Length)}{tail}"];
    }
}

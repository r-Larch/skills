using Npgsql;

namespace WorkState.Tests.Store;

/// <summary>The ids <see cref="QueryPlanTests"/> points its queries at, and what the seed holds.</summary>
public sealed record PlanWorld(
    Guid WorkspaceId,
    Guid BusiestProjectId,
    string BusiestProjectRef,
    Guid BusiestRunId,
    string BusiestRunRef,
    IReadOnlyDictionary<string, long> Counts);

/// <summary>
/// A ~5,000-item workspace in bulk SQL: 8 projects, 20 runs, phases, tasks, sub-tasks and the
/// findings and follow-ups under them (depth 5), with blocks links, criteria, verifications,
/// commits, notes, claims, assumptions, constraints and a ~20k-event journal. The writers are not
/// used: they cost a transaction and a journal round per call, and only the read side is measured.
/// Deterministic — one <see cref="Random"/> seed — so a re-run measures the same data.
/// </summary>
public static class PlanSeed
{
    private static readonly string[] Words =
    [
        "migration", "flaky", "frontier", "rollup", "subtree", "journal", "claim", "lease", "brief",
        "attention", "verify", "criterion", "gate", "phase", "backlog", "deferral", "import", "session",
        "token", "renderer", "search", "index", "planner", "cursor", "stream", "dashboard", "project",
        "archive", "repository", "worktree", "branch", "commit", "orchestrator", "worker", "reviewer",
        "assumption", "constraint", "finding", "followup", "note", "carry", "forward", "decision",
        "incident", "timeout", "retry", "postgres", "trigger", "ancestors", "sequence", "ordering",
        "parser", "markdown", "ledger", "contract", "endpoint", "schema", "column", "refusal", "guard",
        "transition", "status", "review", "blocked", "ready", "cancelled", "superseded", "obsolete",
        "dependency", "cycle", "edge", "link", "derived", "duplicate", "evidence", "measured", "probed",
        "reasoned", "asserted", "red", "green", "check", "test", "suite", "container", "fixture",
        "seed", "workspace", "slug", "qualified", "ambiguous", "resolve", "callable", "printable",
        "notify", "listen", "channel", "heartbeat", "expiry", "sweep", "housekeeping", "stale",
    ];

    private static readonly string[] ProjectRefs =
        ["autowork", "workstate", "nomos", "extract", "skills", "dikaia", "ledger", "portal"];

    private static readonly int[] RunsPerProject = [4, 3, 3, 2, 2, 2, 2, 2];

    private static readonly string[] Closed = ["done", "cancelled", "superseded"];

    private sealed class Row
    {
        public Guid Id;
        public Guid? Parent;
        public int Depth;
        public string Kind = "";
        public string Ref = "";
        public decimal Seq;
        public string Title = "";
        public string? Intent;
        public string Status = "open";
        public short Priority;
        public string? VerifyCmd;
        public string Risk = "";
        public string? Severity;
        public DateTime Opened;
        public DateTime? Started;
        public DateTime? Closed;
        public Guid RunId;
        public DateTime? ArchivedAt;

        public bool IsClosed => PlanSeed.Closed.Contains(Status);
    }

    private sealed record Ev(DateTime At, Guid ItemId, string Kind, string Role, string Summary, Guid? Session);

    public static async Task<PlanWorld> CreateAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var random = new Random(5000);
        var now = DateTime.UtcNow;
        var origin = now.AddDays(-90);
        var workspace = Guid.CreateVersion7();

        string Sentence(int min, int max)
            => string.Join(' ', Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => Words[random.Next(Words.Length)]));

        DateTime Before(DateTime at) => at > now ? now.AddMinutes(-random.Next(1, 600)) : at;

        var rows = new List<Row>();
        var runs = new List<(Row Run, Row Project, double Progress)>();

        Row Add(Row? parent, string kind, string reference, decimal seq, Guid runId, DateTime opened)
        {
            var row = new Row
            {
                Id = Guid.CreateVersion7(),
                Parent = parent?.Id,
                Depth = parent is null ? 0 : parent.Depth + 1,
                Kind = kind,
                Ref = reference,
                Seq = seq,
                Title = Sentence(3, 7),
                Intent = random.NextDouble() < 0.8 ? Sentence(8, 20) : null,
                RunId = runId,
                Opened = Before(opened),
            };
            rows.Add(row);
            return row;
        }

        void Close(Row row, string status, DateTime from)
        {
            row.Status = status;
            row.Started ??= Before(from.AddHours(random.Next(1, 48)));
            row.Closed = Before(row.Started.Value.AddHours(random.Next(1, 72)));
        }

        void Start(Row row, string status, DateTime from)
        {
            row.Status = status;
            // A fifth started long ago: in_progress with no live claim, the stale attention line.
            row.Started = random.NextDouble() < 0.2 ? now.AddHours(-random.Next(5, 200)) : Before(from.AddHours(random.Next(1, 48)));
        }

        var runIndex = 0;
        for (var p = 0; p < ProjectRefs.Length; p++)
        {
            var projectOpened = origin.AddDays(p * 3);
            var project = Add(null, "project", ProjectRefs[p], p + 1, Guid.Empty, projectOpened);
            project.Status = "in_progress";
            project.Started = projectOpened;
            project.Title = $"{ProjectRefs[p]} {Sentence(2, 4)}";
            if (p == ProjectRefs.Length - 1)
            {
                project.ArchivedAt = now.AddDays(-5);
            }

            for (var r = 0; r < RunsPerProject[p]; r++, runIndex++)
            {
                var busiest = runIndex == 0;
                var progress = busiest ? 0.45
                    : p == ProjectRefs.Length - 1 || runIndex % 4 == 3 ? 1.0
                    : 0.1 + random.NextDouble() * 0.8;
                var runOpened = projectOpened.AddDays(r * 10 + random.Next(0, 5));
                var run = Add(project, "run", $"m{r + 1}", r + 1, Guid.Empty, runOpened);
                run.RunId = run.Id;
                runs.Add((run, project, progress));

                var phases = busiest ? 8 : 5;
                var tasksPerPhase = busiest ? 12 : 8;
                var findings = 0;
                var followups = 0;

                for (var ph = 0; ph < phases; ph++)
                {
                    var x = (ph + 0.5) / phases;
                    var stage = x < progress - 0.15 ? 0 : x > progress + 0.15 ? 2 : 1;
                    var phase = Add(run, "phase", $"{ph + 1}", ph + 1, run.Id, runOpened.AddDays(ph * 3));

                    for (var t = 0; t < tasksPerPhase; t++)
                    {
                        var task = Add(phase, "task", $"{ph + 1}.{t + 1}", t + 1, run.Id, phase.Opened.AddHours(t * 4));
                        task.VerifyCmd = $"dotnet test --filter FullyQualifiedName~{ProjectRefs[p]}.{run.Ref}.T{ph + 1}_{t + 1}";
                        task.Priority = (short)(random.NextDouble() < 0.15 ? random.Next(1, 3) : 0);
                        if (random.NextDouble() < 0.1)
                        {
                            task.Risk = random.NextDouble() < 0.5 ? "schema" : "auth,schema";
                        }

                        var roll = random.NextDouble();
                        switch (stage)
                        {
                            case 0:
                                Close(task, roll < 0.95 ? "done" : "cancelled", task.Opened);
                                break;
                            case 1 when roll < 0.35:
                                Close(task, "done", task.Opened);
                                break;
                            case 1 when roll < 0.55:
                                Start(task, "in_progress", task.Opened);
                                break;
                            case 1 when roll < 0.65:
                                Start(task, "review", task.Opened);
                                break;
                            case 1 when roll < 0.70:
                                Start(task, "blocked", task.Opened);
                                break;
                            default:
                                task.Status = random.NextDouble() < 0.1 ? "ready" : "open";
                                break;
                        }

                        var subtasks = busiest ? random.Next(2, 9) : random.Next(1, 6);
                        for (var s = 0; s < subtasks; s++)
                        {
                            var sub = Add(task, "subtask", $"{task.Ref}.{s + 1}", s + 1, run.Id, task.Opened.AddMinutes(s * 20));
                            if (task.Status == "done")
                            {
                                Close(sub, "done", sub.Opened);
                            }
                            else if (task.Status == "cancelled")
                            {
                                Close(sub, "cancelled", sub.Opened);
                            }
                            else if (task.Started is not null)
                            {
                                var subRoll = random.NextDouble();
                                if (subRoll < 0.4)
                                {
                                    Close(sub, "done", sub.Opened);
                                }
                                else if (subRoll < 0.6)
                                {
                                    Start(sub, "in_progress", sub.Opened);
                                }
                            }

                            var extraSeq = 1000m;
                            if (random.NextDouble() < 0.15)
                            {
                                Leaf(sub, "finding", $"F-{++findings}", extraSeq++, stage);
                            }

                            if (random.NextDouble() < 0.10)
                            {
                                Leaf(sub, "followup", $"FU-{++followups}", extraSeq, stage);
                            }
                        }

                        var taskExtra = 1000m;
                        if (random.NextDouble() < 0.2)
                        {
                            Leaf(task, "finding", $"F-{++findings}", taskExtra++, stage);
                        }

                        if (random.NextDouble() < 0.15)
                        {
                            Leaf(task, "followup", $"FU-{++followups}", taskExtra, stage);
                        }
                    }

                    var tasks = rows.Where(x => x.Parent == phase.Id).ToList();
                    if (tasks.All(x => x.IsClosed))
                    {
                        // A fifth of the finished phases are left ungated, which attention reports.
                        if (random.NextDouble() < 0.8)
                        {
                            Close(phase, "done", phase.Opened);
                        }
                        else
                        {
                            Start(phase, "in_progress", phase.Opened);
                        }
                    }
                    else if (tasks.Any(x => x.Started is not null))
                    {
                        Start(phase, "in_progress", phase.Opened);
                    }
                }

                if (progress >= 1.0 && rows.Where(x => x.Parent == run.Id).All(x => x.IsClosed))
                {
                    Close(run, "done", run.Opened);
                }
                else
                {
                    Start(run, "in_progress", run.Opened);
                }
            }

            for (var b = 0; b < 30; b++)
            {
                var backlog = Add(project, "followup", $"B-{b + 1}", 1000 + b, Guid.Empty, project.Opened.AddDays(random.Next(0, 80)));
                if (random.NextDouble() < 0.35)
                {
                    Close(backlog, "done", backlog.Opened);
                }
            }
        }

        void Leaf(Row parent, string kind, string reference, decimal seq, int stage)
        {
            var leaf = Add(parent, kind, reference, seq, parent.RunId, parent.Opened.AddHours(random.Next(1, 30)));
            if (kind == "finding")
            {
                leaf.Severity = random.NextDouble() switch { < 0.1 => "blocker", < 0.5 => "major", _ => "minor" };
            }

            var closeChance = stage == 0 ? 0.9 : stage == 1 ? 0.4 : 0.1;
            if (random.NextDouble() < closeChance)
            {
                Close(leaf, "done", leaf.Opened);
            }
        }

        foreach (var row in rows.Where(r => r.Kind == "project" && r.Status == "in_progress"))
        {
            row.Started ??= row.Opened;
        }

        await ExecAsync(connection, "INSERT INTO workspace (id, slug) VALUES (@ws, 'explain-5k');", ct, ("ws", workspace));

        // ---- items, parent before child: the BEFORE trigger reads the parent's ancestors.
        foreach (var level in rows.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            var batch = level.ToList();
            await ExecAsync(
                connection,
                """
                INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, intent, status, resolution,
                                  priority, verify_cmd, risk_tags, severity, opened_at, started_at, closed_at,
                                  closed_by, archived_at)
                SELECT u.id, @ws, u.parent, u.kind, u.ref, u.seq, u.title, u.intent, u.status::item_status,
                       CASE WHEN u.status = 'done' THEN 'accepted'::item_resolution END,
                       u.priority, u.verify, coalesce(string_to_array(nullif(u.risk, ''), ','), '{}'),
                       u.severity, u.opened, u.started, u.closed,
                       CASE WHEN u.closed IS NOT NULL THEN 'seed' END, u.archived
                FROM   unnest(@ids, @parents, @kinds, @refs, @seqs, @titles, @intents, @statuses, @priorities,
                              @verifies, @risks, @severities, @opened, @started, @closed, @archived)
                       AS u(id, parent, kind, ref, seq, title, intent, status, priority, verify, risk, severity,
                            opened, started, closed, archived);
                """,
                ct,
                ("ws", workspace),
                ("ids", batch.Select(r => r.Id).ToArray()),
                ("parents", batch.Select(r => r.Parent).ToArray()),
                ("kinds", batch.Select(r => r.Kind).ToArray()),
                ("refs", batch.Select(r => r.Ref).ToArray()),
                ("seqs", batch.Select(r => r.Seq).ToArray()),
                ("titles", batch.Select(r => r.Title).ToArray()),
                ("intents", batch.Select(r => r.Intent).ToArray()),
                ("statuses", batch.Select(r => r.Status).ToArray()),
                ("priorities", batch.Select(r => r.Priority).ToArray()),
                ("verifies", batch.Select(r => r.VerifyCmd).ToArray()),
                ("risks", batch.Select(r => r.Risk).ToArray()),
                ("severities", batch.Select(r => r.Severity).ToArray()),
                ("opened", batch.Select(r => r.Opened).ToArray()),
                ("started", batch.Select(r => r.Started).ToArray()),
                ("closed", batch.Select(r => r.Closed).ToArray()),
                ("archived", batch.Select(r => r.ArchivedAt).ToArray()));
        }

        var sessions = Enumerable.Range(1, 40).Select(_ => Guid.CreateVersion7()).ToArray();
        await ExecAsync(
            connection,
            """
            INSERT INTO agent_session (id, workspace_id, name, role, first_seen_at, last_seen_at)
            SELECT u.id, @ws, 'agent-' || u.n, CASE WHEN u.n % 5 = 0 THEN 'orchestrator' ELSE 'worker' END::actor_role,
                   now() - (u.n || ' days')::interval, now() - (u.n || ' hours')::interval
            FROM   unnest(@ids) WITH ORDINALITY AS u(id, n);
            """,
            ct,
            ("ws", workspace),
            ("ids", sessions));

        Guid Session() => sessions[random.Next(sessions.Length)];

        var events = new List<Ev>();
        foreach (var row in rows)
        {
            events.Add(new Ev(row.Opened, row.Id, "created", "orchestrator", $"created {row.Kind} {row.Ref}", Session()));
            if (row.Started is { } started)
            {
                events.Add(new Ev(started, row.Id, "status", "worker", $"{row.Ref} open -> in_progress", Session()));
            }

            if (row.Closed is { } closed)
            {
                events.Add(new Ev(closed, row.Id, "status", "orchestrator", $"{row.Ref} -> {row.Status}", Session()));
            }
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO run_config (item_id, project_item_id, branch, serialization_key)
            SELECT u.run, u.project, 'ws/' || u.ref, 'tree/' || u.run
            FROM   unnest(@runs, @projects, @refs) AS u(run, project, ref);
            """,
            ct,
            ("runs", runs.Select(r => r.Run.Id).ToArray()),
            ("projects", runs.Select(r => r.Project.Id).ToArray()),
            ("refs", runs.Select(r => $"{r.Project.Ref}-{r.Run.Ref}").ToArray()));

        // ---- links: blocks edges forward in document order (never a cycle), derived_from for follow-ups.
        var links = new List<(Guid From, Guid To, string Type)>();
        foreach (var phaseGroup in rows.Where(r => r.Kind == "task").GroupBy(r => r.Parent))
        {
            var tasks = phaseGroup.OrderBy(t => t.Seq).ToList();
            for (var i = 1; i < tasks.Count; i++)
            {
                if (random.NextDouble() < 0.35)
                {
                    links.Add((tasks[i - 1].Id, tasks[i].Id, "blocks"));
                }

                if (i >= 3 && random.NextDouble() < 0.1)
                {
                    links.Add((tasks[i - 3].Id, tasks[i].Id, "blocks"));
                }
            }
        }

        foreach (var runGroup in rows.Where(r => r.Kind == "phase").GroupBy(r => r.Parent))
        {
            var phases = runGroup.OrderBy(x => x.Seq).ToList();
            for (var i = 1; i < phases.Count; i++)
            {
                var last = rows.Where(r => r.Parent == phases[i - 1].Id && r.Kind == "task").MaxBy(r => r.Seq);
                var first = rows.Where(r => r.Parent == phases[i].Id && r.Kind == "task").MinBy(r => r.Seq);
                if (last is not null && first is not null && random.NextDouble() < 0.5)
                {
                    links.Add((last.Id, first.Id, "blocks"));
                }
            }
        }

        var byId = rows.ToDictionary(r => r.Id);
        foreach (var followup in rows.Where(r => r.Kind == "followup" && r.Parent is not null))
        {
            links.Add((followup.Id, followup.Parent!.Value, "derived_from"));
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO item_link (id, workspace_id, from_item_id, to_item_id, type)
            SELECT gen_random_uuid(), @ws, u.f, u.t, u.type::link_type
            FROM   unnest(@froms, @tos, @types) AS u(f, t, type);
            """,
            ct,
            ("ws", workspace),
            ("froms", links.Select(l => l.From).ToArray()),
            ("tos", links.Select(l => l.To).ToArray()),
            ("types", links.Select(l => l.Type).ToArray()));

        foreach (var link in links)
        {
            events.Add(new Ev(byId[link.To].Opened.AddMinutes(5), link.From, "linked", "orchestrator", "linked", Session()));
        }

        // ---- criteria: three per task, one per sub-task; checked once the item is done.
        var criteria = new List<(Guid Item, int Seq, string Text, bool Checked)>();
        foreach (var row in rows.Where(r => r.Kind is "task" or "subtask"))
        {
            var count = row.Kind == "task" ? 3 : 1;
            for (var c = 1; c <= count; c++)
            {
                criteria.Add((row.Id, c, Sentence(4, 9), row.Status == "done" || (row.Started is not null && random.NextDouble() < 0.3)));
            }
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO item_criterion (id, item_id, seq, text, checked, checked_at)
            SELECT gen_random_uuid(), u.item, u.seq, u.text, u.checked, CASE WHEN u.checked THEN now() END
            FROM   unnest(@items, @seqs, @texts, @checks) AS u(item, seq, text, checked);
            """,
            ct,
            ("items", criteria.Select(c => c.Item).ToArray()),
            ("seqs", criteria.Select(c => c.Seq).ToArray()),
            ("texts", criteria.Select(c => c.Text).ToArray()),
            ("checks", criteria.Select(c => c.Checked).ToArray()));

        // ---- verifications and commits on finished work; a tenth were red first.
        var verifications = new List<(Guid Item, string Command, bool Passed, string RunBy, DateTime At)>();
        var commits = new List<(Guid Item, string Sha, DateTime At)>();
        foreach (var row in rows.Where(r => r.Kind is "task" or "subtask" && r.Status == "done"))
        {
            var command = row.VerifyCmd ?? $"dotnet test --filter {row.Ref}";
            if (random.NextDouble() < 0.1)
            {
                verifications.Add((row.Id, command, false, "worker", row.Closed!.Value.AddHours(-2)));
            }

            if (random.NextDouble() < 0.6)
            {
                verifications.Add((row.Id, command, true, "worker", row.Closed!.Value.AddHours(-1)));
            }

            // A few closed without the orchestrator's run: the done-unverified attention line.
            if (random.NextDouble() < 0.95)
            {
                verifications.Add((row.Id, command, true, "orchestrator", row.Closed!.Value));
            }

            if (row.Kind == "task")
            {
                commits.Add((row.Id, Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant(), row.Closed!.Value));
            }
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO verification (id, workspace_id, item_id, command, result_line, passed, run_by, at)
            SELECT gen_random_uuid(), @ws, u.item, u.command,
                   CASE WHEN u.passed THEN 'Passed!  - Failed: 0, Passed: 12' ELSE 'Failed!  - Failed: 1, Passed: 11' END,
                   u.passed, u.run_by::verify_runner, u.at
            FROM   unnest(@items, @commands, @passed, @runBys, @ats) AS u(item, command, passed, run_by, at);

            INSERT INTO artifact (id, item_id, kind, value, at)
            SELECT gen_random_uuid(), u.item, 'commit', u.sha, u.at
            FROM   unnest(@commitItems, @shas, @commitAts) AS u(item, sha, at);
            """,
            ct,
            ("ws", workspace),
            ("items", verifications.Select(v => v.Item).ToArray()),
            ("commands", verifications.Select(v => v.Command).ToArray()),
            ("passed", verifications.Select(v => v.Passed).ToArray()),
            ("runBys", verifications.Select(v => v.RunBy).ToArray()),
            ("ats", verifications.Select(v => v.At).ToArray()),
            ("commitItems", commits.Select(c => c.Item).ToArray()),
            ("shas", commits.Select(c => c.Sha).ToArray()),
            ("commitAts", commits.Select(c => c.At).ToArray()));

        events.AddRange(verifications.Select(v => new Ev(v.At, v.Item, "verified", v.RunBy, $"verified {v.Passed}", Session())));
        events.AddRange(commits.Select(c => new Ev(c.At.AddMinutes(-10), c.Item, "reported", "worker", "reported done", Session())));

        // ---- claims: live, lapsed-but-unreleased, and released history.
        var claims = new List<(Guid Item, Guid Session, DateTime Acquired, DateTime Expires, DateTime? Released, string? Reason)>();
        foreach (var row in rows.Where(r => r.Kind is "task" or "subtask" && r.Started is not null))
        {
            var session = Session();
            if (row.Status == "in_progress")
            {
                var roll = random.NextDouble();
                if (roll < 0.6)
                {
                    claims.Add((row.Id, session, now.AddMinutes(-random.Next(5, 120)), now.AddMinutes(random.Next(10, 60)), null, null));
                }
                else if (roll < 0.8)
                {
                    claims.Add((row.Id, session, now.AddHours(-random.Next(3, 30)), now.AddHours(-random.Next(1, 3)), null, null));
                }
            }
            else if (row.Closed is { } closed && random.NextDouble() < 0.5)
            {
                claims.Add((row.Id, session, row.Started!.Value, closed.AddMinutes(30), closed, "released"));
            }
            else if (random.NextDouble() < 0.1)
            {
                claims.Add((row.Id, session, row.Started!.Value, row.Started!.Value.AddMinutes(30), row.Started!.Value.AddMinutes(31), "expired"));
            }
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO claim (id, item_id, serialization_key, key_workspace_id, agent_name, role, acquired_at,
                               heartbeat_at, expires_at, released_at, release_reason, session_id)
            SELECT gen_random_uuid(), u.item, 'tree/' || u.item, @ws, 'agent', 'worker'::actor_role, u.acquired,
                   u.acquired, u.expires, u.released, u.reason, u.session
            FROM   unnest(@items, @sessions, @acquired, @expires, @released, @reasons)
                   AS u(item, session, acquired, expires, released, reason);
            """,
            ct,
            ("ws", workspace),
            ("items", claims.Select(c => c.Item).ToArray()),
            ("sessions", claims.Select(c => c.Session).ToArray()),
            ("acquired", claims.Select(c => c.Acquired).ToArray()),
            ("expires", claims.Select(c => c.Expires).ToArray()),
            ("released", claims.Select(c => c.Released).ToArray()),
            ("reasons", claims.Select(c => c.Reason).ToArray()));

        events.AddRange(claims.Select(c => new Ev(c.Acquired, c.Item, "claim_acquired", "worker", "claimed", c.Session)));

        // ---- notes: most consumed; the rest addressed to open and closed items alike.
        var noteKinds = new[] { "carry_forward", "correction", "decision", "do_not_rederive", "environment", "incident", "observation" };
        var workItems = rows.Where(r => r.Kind is "task" or "subtask" or "phase").ToArray();
        var notes = new List<(Guid? From, Guid? To, string Kind, string Body, bool Consumed, DateTime At)>();
        for (var n = 0; n < 900; n++)
        {
            var from = workItems[random.Next(workItems.Length)];
            Row? to = random.NextDouble() < 0.6 ? workItems[random.Next(workItems.Length)] : null;
            var at = Before(from.Opened.AddHours(random.Next(1, 200)));
            notes.Add((from.Id, to?.Id, noteKinds[random.Next(noteKinds.Length)], Sentence(10, 30), random.NextDouble() < 0.7, at));
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO note (id, workspace_id, from_item_id, to_item_id, kind, body, consumed_at, created_at)
            SELECT gen_random_uuid(), @ws, u.f, u.t, u.kind::note_kind, u.body,
                   CASE WHEN u.consumed THEN u.at + interval '1 hour' END, u.at
            FROM   unnest(@froms, @tos, @kinds, @bodies, @consumed, @ats) AS u(f, t, kind, body, consumed, at);
            """,
            ct,
            ("ws", workspace),
            ("froms", notes.Select(x => x.From).ToArray()),
            ("tos", notes.Select(x => x.To).ToArray()),
            ("kinds", notes.Select(x => x.Kind).ToArray()),
            ("bodies", notes.Select(x => x.Body).ToArray()),
            ("consumed", notes.Select(x => x.Consumed).ToArray()),
            ("ats", notes.Select(x => x.At).ToArray()));

        events.AddRange(notes.Select(x => new Ev(x.At, x.From!.Value, "note", "worker", "note", Session())));

        // ---- assumptions, numbered per run; constraints, numbered per workspace.
        var assumptions = new List<(Guid Scope, string Ref, bool Open, Guid? Revisit)>();
        foreach (var (run, _, _) in runs)
        {
            var runTasks = rows.Where(r => r.RunId == run.Id && r.Kind == "task").ToArray();
            var count = random.Next(5, 11);
            for (var a = 1; a <= count; a++)
            {
                var scope = random.NextDouble() < 0.5 ? run : runTasks[random.Next(runTasks.Length)];
                Guid? revisit = random.NextDouble() < 0.3 ? runTasks[random.Next(runTasks.Length)].Id : null;
                assumptions.Add((scope.Id, $"A{a}", random.NextDouble() < 0.5, revisit));
            }
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO assumption (id, workspace_id, scope_item_id, ref, statement, evidence, reversal,
                                    revisit_item_id, state, resolved_at)
            SELECT gen_random_uuid(), @ws, u.scope, u.ref, 'the ' || u.ref || ' premise holds', 'reasoned', 'medium',
                   u.revisit, CASE WHEN u.open THEN 'open' ELSE 'confirmed' END::assumption_state,
                   CASE WHEN u.open THEN NULL ELSE now() END
            FROM   unnest(@scopes, @refs, @opens, @revisits) AS u(scope, ref, open, revisit);
            """,
            ct,
            ("ws", workspace),
            ("scopes", assumptions.Select(a => a.Scope).ToArray()),
            ("refs", assumptions.Select(a => a.Ref).ToArray()),
            ("opens", assumptions.Select(a => a.Open).ToArray()),
            ("revisits", assumptions.Select(a => a.Revisit).ToArray()));

        var constraintScopes = new Guid?[60];
        for (var c = 0; c < constraintScopes.Length; c++)
        {
            constraintScopes[c] = random.NextDouble() switch
            {
                < 0.3 => null,
                < 0.6 => runs[random.Next(runs.Count)].Project.Id,
                _ => runs[random.Next(runs.Count)].Run.Id,
            };
        }

        await ExecAsync(
            connection,
            """
            INSERT INTO constraint_rule (id, workspace_id, scope_item_id, ref, kind, text, route, covers_risk)
            SELECT gen_random_uuid(), @ws, u.scope, 'C' || u.n,
                   (ARRAY['command','domain_rule','generated_code','red_line','stack_fact'])[1 + u.n % 5]::constraint_kind,
                   'rule ' || u.n, CASE WHEN u.n % 7 = 0 THEN NULL ELSE 'ask the controller' END,
                   CASE WHEN u.n % 9 = 0 THEN ARRAY['schema'] ELSE '{}'::text[] END
            FROM   unnest(@scopes) WITH ORDINALITY AS u(scope, n);
            """,
            ct,
            ("ws", workspace),
            ("scopes", constraintScopes));

        // ---- the journal, in time order so seq rises with at, topped up to ~20k with status churn.
        var churn = rows.Where(r => r.Started is not null).ToArray();
        while (events.Count < 20_000)
        {
            var row = churn[random.Next(churn.Length)];
            events.Add(new Ev(Before(row.Started!.Value.AddMinutes(random.Next(1, 600))), row.Id, "status", "worker", $"{row.Ref} review -> in_progress", Session()));
        }

        events.Sort((a, b) => a.At.CompareTo(b.At));
        await ExecAsync(
            connection,
            """
            INSERT INTO event (workspace_id, item_id, at, actor, role, kind, summary, session_id)
            SELECT @ws, u.item, u.at, 'agent', u.role::actor_role, u.kind, u.summary, u.session
            FROM   unnest(@items, @ats, @roles, @kinds, @summaries, @sessions) WITH ORDINALITY
                   AS u(item, at, role, kind, summary, session, n)
            ORDER  BY u.n;
            """,
            ct,
            ("ws", workspace),
            ("items", events.Select(e => e.ItemId).ToArray()),
            ("ats", events.Select(e => e.At).ToArray()),
            ("roles", events.Select(e => e.Role).ToArray()),
            ("kinds", events.Select(e => e.Kind).ToArray()),
            ("summaries", events.Select(e => e.Summary).ToArray()),
            ("sessions", events.Select(e => e.Session).ToArray()));

        var counts = new Dictionary<string, long>();
        foreach (var table in new[]
                 {
                     "item", "item_link", "item_criterion", "verification", "artifact", "note", "claim",
                     "assumption", "constraint_rule", "agent_session", "event",
                 })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table};", connection);
            counts[table] = (long)(await count.ExecuteScalarAsync(ct))!;
        }

        await using (var shape = new NpgsqlCommand(
            "SELECT kind || '@' || depth, count(*) FROM item GROUP BY kind, depth ORDER BY depth, kind;", connection))
        await using (var reader = await shape.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                counts[$"item:{reader.GetString(0)}"] = reader.GetInt64(1);
            }
        }

        var busiestRun = runs[0];
        return new PlanWorld(
            workspace,
            busiestRun.Project.Id,
            busiestRun.Project.Ref,
            busiestRun.Run.Id,
            busiestRun.Run.Ref,
            counts);
    }

    private static async Task ExecAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}

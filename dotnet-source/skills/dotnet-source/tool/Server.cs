// Server.cs — the opt-in `serve` daemon. This is keep-alive layer 2 for TIER 2.
//
// The parse index (Index.cs) keeps Tier 1 fast, but it can only hold extracted declaration
// records. Tier 2 needs live SyntaxTrees and a Roslyn Compilation, which nothing on disk can
// hold — on Nomos, assembling them costs ~15s per find-usages. The daemon builds the Solution
// ONCE and keeps it resident, so repeat semantic queries skip that entirely.
//
// Deliberate design choices:
//  * OPT-IN. Commands probe for a live daemon and fall back to stateless if absent; they never
//    auto-spawn one. Auto-spawn turns a burst of parallel agent commands into a thundering herd
//    of processes each building a full Solution.
//  * Keyed by solution root AND TOOL BUILD ID. A rebuilt tool must not be served by a daemon
//    running the old logic — that's a silent-wrong-answer generator.
//  * NamedPipeStream, which .NET implements over unix domain sockets on macOS/Linux, so one code
//    path covers every OS.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotnetSource;

sealed record Request(string Cmd, string[] Args);
sealed record Response(string Output, int Exit);

static class Server
{
    static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public static string PipeName(string root) => "dotnet-source-" + Key(root);

    /// <summary>
    /// Identity of a daemon: the solution root AND the tool build. Two agents serving two
    /// DIFFERENT solutions get different keys, so their daemons are fully independent — that's the
    /// common parallel-agent case and it needs no coordination at all. The MVID is in the key so a
    /// rebuilt tool is never served by a daemon still running the old logic.
    /// </summary>
    static string Key(string root) =>
        DeclIndex.Hash($"{root.ToLowerInvariant()}|{typeof(Server).Assembly.ManifestModule.ModuleVersionId}");

    /// <summary>
    /// Presence marker for a live daemon. Held open by the daemon with FileOptions.DeleteOnClose,
    /// which makes it self-cleaning: the OS drops the handle even on a hard kill, so a stale marker
    /// can't outlive the process that made it.
    ///
    /// It exists so a client can tell "no daemon" (skip the probe entirely — stateless commands
    /// shouldn't pay for a connect timeout) apart from "daemon is busy serving someone else" (wait
    /// and queue, instead of silently falling back to the 15s stateless path).
    /// </summary>
    static string PresencePath(string root) => Path.Combine(Paths.CacheRoot, "daemons", $"{Key(root)}.pid");

    static int? LiveDaemonPid(string root)
    {
        try
        {
            var path = PresencePath(root);
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            if (!int.TryParse(sr.ReadLine(), out var pid)) return null;
            try { using var _ = Process.GetProcessById(pid); return pid; }
            catch { return null; }   // marker outlived its process (shouldn't happen; be safe anyway)
        }
        catch { return null; }
    }

    // ---- wire format -------------------------------------------------------------------
    // Length-prefixed UTF-8 JSON, one request + one response per connection. Deliberately the
    // dumbest thing that works — an RPC framework would be more code than the daemon itself.

    static void WriteFrame(Stream s, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        s.Write(BitConverter.GetBytes(bytes.Length));
        s.Write(bytes);
        s.Flush();
    }

    static string? ReadFrame(Stream s)
    {
        var header = new byte[4];
        if (!ReadExactly(s, header)) return null;
        var len = BitConverter.ToInt32(header);
        if (len is < 0 or > 64 * 1024 * 1024) return null;   // refuse an absurd/garbage length
        var body = new byte[len];
        return ReadExactly(s, body) ? Encoding.UTF8.GetString(body) : null;
    }

    static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // ---- client ------------------------------------------------------------------------

    /// <summary>
    /// Ask a live daemon to run this command. Returns null if none is listening (caller then runs
    /// stateless).
    ///
    /// The daemon serves ONE request at a time on purpose — the command handlers capture output by
    /// swapping Console.Out, which is process-global, so concurrent handling would interleave two
    /// agents' results. Serializing is correct; the cost is that a second agent must wait its turn.
    /// So: when the presence marker says a daemon is alive we wait (queueing in the OS until it
    /// calls WaitForConnection again), and when there's no marker we don't probe at all.
    /// </summary>
    public static Response? TryAsk(string root, Request req)
    {
        if (LiveDaemonPid(root) is null) return null;   // no daemon: cost is one File.Exists, not a timeout
        return Ask(root, req, timeoutMs: 20_000);
    }

    static Response? Ask(string root, Request req, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            WriteFrame(pipe, JsonSerializer.Serialize(req));
            var raw = ReadFrame(pipe);
            return raw is null ? null : JsonSerializer.Deserialize<Response>(raw);
        }
        catch { return null; }   // died mid-request: caller falls back to stateless
    }

    public static int Status(Args a, Stopwatch sw)
    {
        var root = Discovery.ResolveRootOnly(a);
        var pid = LiveDaemonPid(root);
        var resp = pid is null ? null : Ask(root, new Request("ping", []), 5000);

        Console.WriteLine($"root      {root}");
        Console.WriteLine($"pipe      {PipeName(root)}");
        Console.WriteLine($"daemon    {(pid is null
            ? "not running — Tier 2 runs stateless (~15s per query). `ds serve` to warm it."
            : $"running (pid {pid}) — Tier 2 is warm")}");
        if (resp is not null) Console.WriteLine(resp.Output.TrimEnd());
        if (pid is not null) Console.WriteLine($"log       {LogPath(root)}");
        Console.WriteLine($"cache     {Paths.CacheRoot}");
        Console.WriteLine($"version   {Program.Version}");
        return 0;
    }

    // ---- daemon ------------------------------------------------------------------------

    public static int Serve(Args a, Stopwatch sw)
    {
        var root = Discovery.ResolveRootOnly(a);

        if (a.Has("stop"))
        {
            var r = Ask(root, new Request("stop", []), 5000);
            Console.WriteLine(r is null ? "no daemon running for this solution." : "daemon stopped.");
            return 0;
        }

        if (LiveDaemonPid(root) is { } running)
        {
            Console.WriteLine($"already serving {root} (pid {running}) — nothing to do.");
            return 0;
        }

        // `serve` BACKGROUNDS ITSELF by default. It used to block, which meant an agent that ran
        // `ds serve` hung until its timeout, and a human had to know to append `&`. Neither is
        // acceptable for a command whose entire job is "make later commands fast".
        // --foreground keeps the old blocking behaviour (and is what the detached child runs).
        return a.Has("foreground") ? Foreground(a, sw) : Detach(a, root, sw);
    }

    /// <summary>Spawn a detached copy running --foreground, wait for it to warm, then report.</summary>
    static int Detach(Args a, string root, Stopwatch sw)
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        var host = Environment.ProcessPath ?? "dotnet";
        var log = LogPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        var psi = new ProcessStartInfo(host);
        if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(entry);
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--foreground");
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(log);            // an arg, not an env var: ShellExecute forbids Environment
        foreach (var flag in new[] { "sln", "proj", "root" })
            if (a.Str(flag) is { } v) { psi.ArgumentList.Add($"--{flag}"); psi.ArgumentList.Add(v); }
        if (a.Has("include-generated")) psi.ArgumentList.Add("--include-generated");

        if (OperatingSystem.IsWindows())
        {
            // MUST be ShellExecute on Windows. With UseShellExecute=false .NET creates the process
            // with bInheritHandles=true, so the daemon inherits a DUPLICATE of our caller's stdout
            // handle — even though its own stdout is elsewhere. A caller that CAPTURES our output
            // (`$o = & ds.ps1 serve`, i.e. every agent, and `Measure-Command`) then waits for EOF on
            // a pipe the daemon holds open for its whole 30-minute life: `serve` appears to hang.
            // It only looked fine interactively because an unredirected console has no EOF to wait
            // for. ShellExecute starts the process with no inherited handles, which is the point.
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            // No ShellExecute on unix (it would hand off to xdg-open). Redirecting is enough there:
            // .NET marks inherited descriptors close-on-exec, so the child holds no caller fds.
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
        }

        Process child;
        try { child = Process.Start(psi)!; }
        catch (Exception e) { throw new UserError($"could not start the daemon: {e.Message}"); }

        // Wait until it actually answers, so "started" means warm rather than "probably fine".
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (Ask(root, new Request("ping", []), 200) is { } pong)
            {
                var pid = LiveDaemonPid(root) ?? child.Id;
                Console.WriteLine($"daemon started (pid {pid}) — warm in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine(pong.Output.TrimEnd());
                Console.WriteLine("Tier-2 commands (find-usages/impls/calls/unused) will use it automatically.");
                Console.WriteLine($"`ds serve --stop` to stop it; it also idles out after {IdleTimeout.TotalMinutes:0} min.");
                return 0;
            }

            // Our child exiting is NOT automatically a failure. When several agents `serve` the same
            // solution at once, exactly one child wins the single-instance lock and the rest exit
            // straight away — from those parents' point of view the daemon is still coming up, it
            // just isn't theirs. So only give up once nothing holds the presence marker either.
            if (child.HasExited && LiveDaemonPid(root) is null) break;

            Thread.Sleep(200);
        }

        var waited = (int)sw.Elapsed.TotalSeconds;
        var tail = ReadLogTail(log);
        throw new UserError($"the daemon did not come up (gave up after {waited}s"
                          + (child.HasExited ? $"; the process exited with {child.ExitCode}" : "") + ")."
                          + (tail.Length > 0 ? $"\n--- {log} ---\n{tail}" : $"\nnothing was logged to {log}"));
    }

    static string LogPath(string root) => Path.Combine(Paths.CacheRoot, "daemons", $"{Key(root)}.log");

    static string ReadLogTail(string log)
    {
        try { return string.Join('\n', File.ReadAllLines(log).TakeLast(15)); }
        catch { return ""; }
    }

    /// <summary>Set by --log when we were spawned detached; null when run in the foreground.</summary>
    static string? _log;

    /// <summary>Where the daemon's own messages go: its log file when detached, else stderr.</summary>
    static void Say(string msg)
    {
        if (string.IsNullOrEmpty(_log)) { Console.Error.WriteLine($"dotnet-source: {msg}"); return; }
        try { File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss}  {msg}\n"); }
        catch { /* logging must never take the daemon down */ }
    }

    static int Foreground(Args a, Stopwatch sw)
    {
        _log = a.Str("log");
        var set = Discovery.Resolve(a, a.Has("include-generated"));

        // Single-instance lock AND presence marker in one atomic step. CreateNew is the race
        // resolver: if two agents `serve` the same solution at the same instant, exactly one
        // creates the file and the other is told who won — instead of both spending 5s building a
        // Solution and the loser then dying on a pipe collision.
        // DeleteOnClose means the marker cannot outlive this process, even if it's killed.
        FileStream presence;
        var presencePath = PresencePath(set.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(presencePath)!);
        try
        {
            presence = new FileStream(presencePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            var pid = LiveDaemonPid(set.Root);
            Say($"another daemon is already serving {set.Root}"
              + (pid is null ? " (its marker is stale — retry in a moment)" : $" (pid {pid})"));
            return 0;
        }

        using (presence)
        {
            using var w = new StreamWriter(presence) { AutoFlush = true };
            w.WriteLine(Environment.ProcessId);
            w.WriteLine(set.Root);

            Say($"building the semantic workspace for {set.Root} …");
            var loaded = WorkspaceBuilder.Build(set);
            var state = new ServerState(loaded);
            Say($"warm after {sw.ElapsedMilliseconds} ms ({set.Projects.Count} projects, {set.FileCount} files). "
              + $"Idle timeout {IdleTimeout.TotalMinutes:0} min.");

            return Listen(set, state);
        }
    }

    static int Listen(SolutionSet set, ServerState state)
    {
        using var watcher = StartWatching(state);
        var stop = new ManualResetEventSlim(false);
        var lastUse = DateTime.UtcNow;

        // Idle reaper: a forgotten daemon holding a whole Solution in memory is rude.
        var reaper = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                if (DateTime.UtcNow - lastUse > IdleTimeout) { stop.Set(); break; }
                Thread.Sleep(5000);
            }
        }) { IsBackground = true };
        reaper.Start();

        var pipeName = PipeName(set.Root);
        while (!stop.IsSet)
        {
            NamedPipeServerStream pipe;
            try
            {
                // One instance: requests are served strictly one at a time because the command
                // handlers capture output via the process-global Console.Out. Extra clients queue
                // in the OS until the next WaitForConnection, which is why TryAsk waits when the
                // presence marker says a daemon is alive.
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                pipe.WaitForConnection();
            }
            catch (Exception e)
            {
                // This used to be `catch { break; }`, which meant the daemon vanished without ever
                // saying why. If we can't listen, that's the one thing worth reporting.
                Say($"stopped listening: {e.GetType().Name}: {e.Message}");
                break;
            }

            using (pipe)
            {
                lastUse = DateTime.UtcNow;
                try
                {
                    var raw = ReadFrame(pipe);
                    if (raw is null) continue;
                    var req = JsonSerializer.Deserialize<Request>(raw);
                    if (req is null) continue;

                    if (req.Cmd == "stop") { WriteFrame(pipe, JsonSerializer.Serialize(new Response("", 0))); stop.Set(); break; }

                    var resp = Handle(state, req);
                    WriteFrame(pipe, JsonSerializer.Serialize(resp));
                }
                catch { /* a dead client must never take the daemon down */ }
            }
        }

        Say("daemon stopped.");
        return 0;
    }

    static Response Handle(ServerState state, Request req)
    {
        if (req.Cmd == "ping")
            return new Response($"          {state.Loaded.Set.Projects.Count} projects, "
                              + $"{state.Loaded.Set.FileCount} files, {state.Applied} edit(s) applied since start", 0);

        // Reuse the exact same command handlers, but hand them the warm Solution and capture
        // whatever they'd have printed. One implementation, two transports.
        var stdout = new StringWriter();
        var original = Console.Out;
        Console.SetOut(stdout);
        var exit = 0;
        try
        {
            var a = new Args(req.Args);
            var sw = Stopwatch.StartNew();
            Tier2.Warm = state.Current();
            exit = req.Cmd switch
            {
                "find-usages" => Tier2.FindUsages(a, sw),
                "impls" => Tier2.Impls(a, sw),
                "calls" => Tier2.Calls(a, sw),
                "unused" => Tier2.Unused(a, sw),
                _ => Fail(stdout, $"daemon cannot serve '{req.Cmd}'"),
            };
        }
        catch (UserError e) { stdout.WriteLine($"error: {e.Message}"); exit = 2; }
        catch (Exception e) { stdout.WriteLine($"error: {e.Message}"); exit = 2; }
        finally { Tier2.Warm = null; Console.SetOut(original); }

        return new Response(stdout.ToString(), exit);
    }

    static int Fail(TextWriter w, string msg) { w.WriteLine($"error: {msg}"); return 2; }

    // ---- incremental invalidation ------------------------------------------------------

    static FileSystemWatcher StartWatching(ServerState state)
    {
        var w = new FileSystemWatcher(state.Loaded.Set.Root)
        {
            IncludeSubdirectories = true,
            Filter = "*.cs",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        w.Changed += (_, e) => state.Touch(e.FullPath);
        w.Created += (_, e) => state.Touch(e.FullPath);
        w.Deleted += (_, e) => state.Touch(e.FullPath);
        // Editors save by writing a temp file and renaming over the target, so Renamed is a
        // normal edit, not an exotic case.
        w.Renamed += (_, e) => { state.Touch(e.OldFullPath); state.Touch(e.FullPath); };

        // THE important one. The OS drops events when its buffer overflows — a bulk `git checkout`
        // or branch switch does exactly that. Ignoring Error means quietly serving a stale
        // Solution: right-looking answers, wrong content, no signal. So treat it as "I no longer
        // know what changed" and rebuild from scratch.
        w.Error += (_, _) => state.InvalidateAll();

        w.EnableRaisingEvents = true;
        return w;
    }
}

/// <summary>
/// Holds the warm Solution. Roslyn's Solution is immutable, so updates are an interlocked swap and
/// readers never lock.
/// </summary>
sealed class ServerState(LoadedSolution loaded)
{
    public LoadedSolution Loaded { get; } = loaded;
    public int Applied;

    Solution _solution = loaded.Solution;
    readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _gate = new();
    bool _rebuildAll;

    public void Touch(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        if (Discovery.IsExcludedPath(path)) return;   // bin/obj churn must not invalidate anything
        lock (_gate) _dirty.Add(path);
    }

    public void InvalidateAll()
    {
        lock (_gate) { _rebuildAll = true; _dirty.Clear(); }
    }

    /// <summary>The Solution with all pending edits applied. Called on the request path.</summary>
    public LoadedSolution Current()
    {
        List<string> dirty;
        bool all;
        lock (_gate) { dirty = [.. _dirty]; _dirty.Clear(); all = _rebuildAll; _rebuildAll = false; }

        if (all)
        {
            var rebuilt = WorkspaceBuilder.Build(Loaded.Set);
            _solution = rebuilt.Solution;
            Applied++;
            return rebuilt;
        }

        if (dirty.Count == 0) return WithSolution(_solution);

        var solution = _solution;
        foreach (var path in dirty)
        {
            var ids = solution.GetDocumentIdsWithFilePath(path);
            if (ids.Length == 0) continue;          // a new file: needs a full rebuild to be placed
            if (!File.Exists(path))
            {
                foreach (var id in ids) solution = solution.RemoveDocument(id);
                continue;
            }
            try
            {
                var text = SourceText.From(File.ReadAllText(path), Encoding.UTF8);
                foreach (var id in ids) solution = solution.WithDocumentText(id, text);
            }
            catch { /* mid-write: the next event will bring it in */ }
        }

        _solution = solution;
        Applied += dirty.Count;
        return WithSolution(solution);
    }

    LoadedSolution WithSolution(Solution s)
    {
        var l = new LoadedSolution { Workspace = Loaded.Workspace, Solution = s, Set = Loaded.Set };
        foreach (var (k, v) in Loaded.ByCsproj) l.ByCsproj[k] = v;
        return l;
    }
}

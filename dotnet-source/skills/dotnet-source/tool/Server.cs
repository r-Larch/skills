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

    public static string PipeName(string root)
    {
        var mvid = typeof(Server).Assembly.ManifestModule.ModuleVersionId;
        return "dotnet-source-" + DeclIndex.Hash($"{root.ToLowerInvariant()}|{mvid}");
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

    /// <summary>Ask a live daemon to run this command. Returns null if none is listening.</summary>
    public static Response? TryAsk(string root, Request req, int timeoutMs = 300)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            WriteFrame(pipe, JsonSerializer.Serialize(req));
            var raw = ReadFrame(pipe);
            return raw is null ? null : JsonSerializer.Deserialize<Response>(raw);
        }
        catch { return null; }   // no daemon, or it died mid-request: caller falls back to stateless
    }

    public static int Status(Args a, Stopwatch sw)
    {
        var set = Discovery.Resolve(a);
        var resp = TryAsk(set.Root, new Request("ping", []));
        Console.WriteLine($"root      {set.Root}");
        Console.WriteLine($"pipe      {PipeName(set.Root)}");
        Console.WriteLine($"daemon    {(resp is null ? "not running (commands run stateless)" : "running — Tier 2 is warm")}");
        if (resp is not null) Console.WriteLine(resp.Output.TrimEnd());
        Console.WriteLine($"cache     {Paths.CacheRoot}");
        Console.WriteLine($"version   {Program.Version}");
        return 0;
    }

    // ---- daemon ------------------------------------------------------------------------

    public static int Serve(Args a, Stopwatch sw)
    {
        var set = Discovery.Resolve(a, a.Has("include-generated"));

        if (a.Has("stop"))
        {
            var r = TryAsk(set.Root, new Request("stop", []));
            Console.WriteLine(r is null ? "no daemon running for this solution." : "daemon stopped.");
            return 0;
        }

        if (TryAsk(set.Root, new Request("ping", [])) is not null)
        {
            Console.WriteLine("a daemon is already serving this solution — nothing to do.");
            return 0;
        }

        Console.Error.WriteLine($"dotnet-source: building the semantic workspace for {set.Root} …");
        var loaded = WorkspaceBuilder.Build(set);
        var state = new ServerState(loaded);
        Console.Error.WriteLine($"dotnet-source: warm after {sw.ElapsedMilliseconds} ms "
                              + $"({set.Projects.Count} projects, {set.FileCount} files). "
                              + $"Idle timeout {IdleTimeout.TotalMinutes:0} min. Ctrl-C to stop.");

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
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnection();
            }
            catch { break; }

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

        Console.Error.WriteLine("dotnet-source: daemon stopped.");
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

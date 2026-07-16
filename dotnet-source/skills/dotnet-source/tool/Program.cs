// Program.cs — subcommand dispatch. One binary, git-style verbs.
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace DotnetSource;

static class Program
{
    public const string Version = "0.1.0";

    static int Main(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") { Usage(); return 1; }

        var cmd = argv[0];
        var a = new Args(argv.Skip(1));
        var sw = Stopwatch.StartNew();

        try
        {
            switch (cmd.ToLowerInvariant())
            {
                case "--version" or "version": Console.WriteLine(Version); return 0;
                case "discover": return Cmd.Discover(a, sw);
                case "search": return Tier1.Search(a, sw);
                case "outline": return Tier1.Outline(a, sw);
                case "tree": return Tier1.Tree(a, sw);
                case "metrics": return Tier1.Metrics(a, sw);
                // Tier 2: use a warm daemon if one happens to be serving this solution, else run
                // stateless. Never auto-spawn one.
                case "find-usages": return Route("find-usages", argv, a, () => Tier2.FindUsages(a, sw));
                case "impls": return Route("impls", argv, a, () => Tier2.Impls(a, sw));
                case "calls": return Route("calls", argv, a, () => Tier2.Calls(a, sw));
                case "unused": return Route("unused", argv, a, () => Tier2.Unused(a, sw));
                case "serve": return Server.Serve(a, sw);
                case "status": return Server.Status(a, sw);
                default:
                    Console.Error.WriteLine($"unknown command: {cmd}");
                    Usage();
                    return 1;
            }
        }
        catch (UserError e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Send the command to a live daemon if one is serving this solution, otherwise run it here.
    /// The probe is cheap (root resolution only, ~300ms connect timeout) and failure is silent by
    /// design: no daemon is the normal case, not an error.
    /// </summary>
    static int Route(string cmd, string[] argv, Args a, Func<int> local)
    {
        try
        {
            var root = Discovery.ResolveRootOnly(a);
            if (Server.TryAsk(root, new Request(cmd, argv.Skip(1).ToArray())) is { } resp)
            {
                Console.Write(resp.Output);
                return resp.Exit;
            }
        }
        catch { /* fall through: the local path reports errors properly */ }
        return local();
    }

    static void Usage() => Console.Error.WriteLine("""
        dotnet-source — source-level navigation for a .NET solution you're editing (Roslyn).

        Tier 1 — syntax only. No build required, works on broken code, SEES PRIVATE MEMBERS.
          search <pattern>        [--kind class,interface,method,prop,field,enum,record] [--regex]
          outline <Type|pattern>  |  outline --file <path.cs>
          tree [namespaceFilter]
          metrics [--top 30] [--sort members|loc|methods|params]

        Tier 2 — semantic. Needs restored packages (`dotnet restore` once); no build needed.
          find-usages <symbol>    every reference incl. declarations, locals, overrides
          impls <TypeOrInterface> [--derived]
          calls <method>          [--callers|--callees]
          unused                  [--kind …] [--proj <csproj>|--type <Ns.Type>] [--include-public]
                                  Reports only NON-public declarations by default (a public member
                                  may be called from outside the solution). --include-public to
                                  include them — right for a leaf app, misleading for a library.
                                  Here --proj/--type only scope WHAT IS REPORTED; usages are always
                                  searched across the whole solution.

        Keep-alive (optional — Tier 2 only)
          serve [--sln <path>]    START AND RETURN. Backgrounds itself and holds the Solution in
                                  memory, so repeat Tier-2 queries skip the ~15s workspace build
                                  (find-usages: ~15s -> ~0.2s). Tier-2 commands then use it
                                  automatically. Returns once it's warm.
                                  A daemon is identified by its SOLUTION ROOT, so: parallel agents
                                  on different solutions get independent daemons and never collide;
                                  any number of agents may `serve` the same solution — exactly one
                                  is started and the rest just wait for it.
            --foreground          block instead (Ctrl-C to stop) — for debugging the daemon
            --stop                stop the daemon serving this solution
          status                  is a daemon serving this solution? (pid, log path)

        Utility
          discover [--semantic]   what the tool sees: projects, files, reference health. Start here
                                  if counts look wrong.
          version

        Target selection (EVERY command, including serve/status):
          --sln <path>            the solution to work on. Prefer this — it is authoritative, and
                                  for `serve` it is what identifies the daemon.
          --root <dir>            no solution file: scan this directory tree
          --proj <csproj>         just this one project. NOTE: this makes the workspace a SINGLE
                                  project, so Tier-2 commands cannot see references from sibling
                                  projects. (`unused` is the exception — there it is only a filter.)
          default: walk up from cwd for *.slnx/*.sln, else treat cwd as root.
          --include-generated     also scan *.g.cs / *.Designer.cs (off by default)
        """);
}

static class Cmd
{
    /// <summary>
    /// Print what discovery resolved. This exists because the failure mode it guards against is
    /// silent: a bad project set produces plausible-looking but wrong output everywhere else.
    /// </summary>
    public static int Discover(Args a, Stopwatch sw)
    {
        var set = Discovery.Resolve(a, a.Has("include-generated"));

        Console.WriteLine($"root       {set.Root}");
        Console.WriteLine($"solution   {(set.SolutionFile is null ? "(none — glob mode)" : set.SolutionFile)}");
        Console.WriteLine($"projects   {set.Projects.Count}");
        Console.WriteLine($"files      {set.FileCount} .cs");
        Console.WriteLine();

        Console.WriteLine($"{"PROJECT",-40} {"TFM",-10} {"FILES",5}  {"REFS",4}  DEFINES");
        foreach (var p in set.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{p.Name,-40} {p.Tfm,-10} {p.Files.Count,5}  {p.ProjectRefs.Count,4}  {string.Join(";", p.DefineConstants)}");

        if (set.Notes.Count > 0)
        {
            Console.WriteLine();
            foreach (var n in set.Notes) Console.WriteLine($"// note: {n}");
        }

        // Self-check: the duplication this whole design guards against would show up here.
        var dupes = set.Projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"// WARNING: {dupes.Count} project name(s) appear more than once — the file set is "
                            + "probably duplicated (a vendored copy or a nested worktree got scanned):");
            foreach (var g in dupes.Take(10))
                foreach (var p in g) Console.WriteLine($"//   {p.Name}  {Discovery.Rel(set.Root, p.CsprojPath)}");
        }

        if (a.Has("semantic")) Semantic(set, a);

        Console.WriteLine();
        Console.WriteLine($"// {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    /// <summary>
    /// Build the Tier-2 Solution and report reference health. CS0246 ("type or namespace not
    /// found") is the signal that reference resolution worked: near-zero means the assets.json +
    /// shared-framework references are complete. A large count means Tier 2 answers would be
    /// wrong, so it's worth being able to see this directly.
    /// </summary>
    static void Semantic(SolutionSet set, Args a)
    {
        Console.WriteLine();
        Console.WriteLine("// building semantic workspace (assets.json + shared frameworks, no MSBuild) …");
        var loaded = WorkspaceBuilder.Build(set);

        Console.WriteLine();
        Console.WriteLine($"{"PROJECT",-40} {"REFS",5} {"CS0246",7} {"ERRORS",7}");
        var totalMissing = 0;
        foreach (var proj in loaded.Solution.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var comp = proj.GetCompilationAsync().GetAwaiter().GetResult();
            var diags = comp?.GetDiagnostics() ?? [];
            var missing = diags.Count(d => d.Id == "CS0246");
            var errors = diags.Count(d => d.Severity == DiagnosticSeverity.Error);
            totalMissing += missing;
            Console.WriteLine($"{proj.Name,-40} {proj.MetadataReferences.Count,5} {missing,7} {errors,7}");
        }

        Console.WriteLine();
        Console.WriteLine(totalMissing == 0
            ? "// references resolved: 0 CS0246 across the solution — Tier 2 is trustworthy."
            : $"// {totalMissing} CS0246 unresolved — Tier 2 may be incomplete for those names. See notes below; "
            + "the usual causes are packages not restored (`dotnet restore`) or .razor components (never compiled).");

        if (a.Has("diag") && totalMissing > 0)
        {
            Console.WriteLine();
            Console.WriteLine("// most common unresolved names:");
            var msgs = loaded.Solution.Projects
                .SelectMany(p => (p.GetCompilationAsync().GetAwaiter().GetResult()?.GetDiagnostics() ?? [])
                    .Where(d => d.Id == "CS0246"))
                .GroupBy(d => d.GetMessage())
                .OrderByDescending(g => g.Count())
                .Take(15);
            foreach (var g in msgs) Console.WriteLine($"//   {g.Count(),5}x  {g.Key}");
        }

        foreach (var n in loaded.Notes.Take(10)) Console.WriteLine($"// note: {n}");
    }
}

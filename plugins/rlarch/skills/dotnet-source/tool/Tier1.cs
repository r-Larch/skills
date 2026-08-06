// Tier1.cs — syntax-only commands: search / outline / tree / metrics.
// No compilation and no reference resolution, so these work on a solution that doesn't build
// and they see private/internal members.
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotnetSource;

static class Tier1
{
    static readonly Dictionary<string, DeclKind[]> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["class"] = [DeclKind.Class],
        ["struct"] = [DeclKind.Struct],
        ["record"] = [DeclKind.Record],
        ["interface"] = [DeclKind.Interface],
        ["enum"] = [DeclKind.Enum],
        ["delegate"] = [DeclKind.Delegate],
        ["type"] = [DeclKind.Class, DeclKind.Struct, DeclKind.Record, DeclKind.Interface, DeclKind.Enum, DeclKind.Delegate],
        ["method"] = [DeclKind.Method],
        ["ctor"] = [DeclKind.Ctor],
        ["prop"] = [DeclKind.Property],
        ["property"] = [DeclKind.Property],
        ["field"] = [DeclKind.Field],
        ["event"] = [DeclKind.Event],
        ["indexer"] = [DeclKind.Indexer],
        ["operator"] = [DeclKind.Operator],
    };

    static HashSet<DeclKind>? KindFilter(Args a)
    {
        var raw = a.Csv("kind");
        if (raw.Count == 0) return null;
        var set = new HashSet<DeclKind>();
        foreach (var k in raw)
        {
            if (!KindAliases.TryGetValue(k, out var kinds))
                throw new UserError($"unknown --kind '{k}'. Known: {string.Join(", ", KindAliases.Keys)}");
            foreach (var x in kinds) set.Add(x);
        }
        return set;
    }

    static List<Decl> Load(Args a, out SolutionSet set)
    {
        set = Discovery.Resolve(a, a.Has("include-generated"));
        return DeclIndex.Load(set);
    }

    // ---- search ------------------------------------------------------------------------

    public static int Search(Args a, Stopwatch sw)
    {
        var pattern = a.At(0) ?? throw new UserError("usage: search <pattern> [--kind …] [--regex]");
        var kinds = KindFilter(a);
        var decls = Load(a, out var set);

        Func<string, bool> match;
        if (a.Has("regex"))
        {
            Regex rx;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch (ArgumentException e) { throw new UserError($"bad --regex: {e.Message}"); }
            match = s => rx.IsMatch(s);
        }
        else match = s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var hits = decls
            .Where(d => kinds is null || kinds.Contains(d.Kind))
            .Where(d => match(d.Name))
            .OrderBy(d => d.Fqn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var d in hits)
            Console.WriteLine($"{d.KindWord,-12} {d.Fqn,-70} {Discovery.Rel(set.Root, d.File)}:{d.Line}"
                            + (d.Modifiers.Length > 0 ? $"   [{d.Modifiers}]" : ""));

        Console.WriteLine();
        Console.WriteLine($"// {hits.Count} hit(s) in {decls.Count} declarations, {set.FileCount} files — {sw.ElapsedMilliseconds} ms");
        return hits.Count > 0 ? 0 : 3;
    }

    // ---- outline -----------------------------------------------------------------------

    public static int Outline(Args a, Stopwatch sw)
    {
        var decls = Load(a, out var set);
        var file = a.Str("file");

        if (file is not null)
        {
            var full = Path.GetFullPath(file);
            var inFile = decls.Where(d => d.File.Equals(full, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(d => d.Line).ToList();
            if (inFile.Count == 0) throw new UserError($"no declarations found in {full} (is it in the solution?)");
            Console.WriteLine($"// {Discovery.Rel(set.Root, full)}");
            foreach (var d in inFile)
                Console.WriteLine($"{d.Line,6}  {(d.IsType ? "" : "  ")}{Vis(d)}{d.Signature}");
            Console.WriteLine();
            Console.WriteLine($"// {inFile.Count} declaration(s) — {sw.ElapsedMilliseconds} ms");
            return 0;
        }

        var pattern = a.At(0) ?? throw new UserError("usage: outline <TypeName|pattern> [--members]  |  outline --file <path.cs>");

        var types = decls.Where(d => d.IsType)
            .Where(d => d.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                     || d.Fqn.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (types.Count == 0) throw new UserError($"no type matching '{pattern}'");

        // Partial parts share an FQN — merge them and list every part's file.
        foreach (var group in types.GroupBy(t => t.Fqn, StringComparer.Ordinal)
                                   .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var parts = group.OrderBy(t => t.File, StringComparer.OrdinalIgnoreCase).ToList();
            var head = parts[0];

            Console.WriteLine($"{Vis(head)}{head.Signature}");
            foreach (var p in parts)
                Console.WriteLine($"    // {Discovery.Rel(set.Root, p.File)}:{p.Line}"
                                + (parts.Count > 1 ? "   (partial part)" : ""));

            var members = decls.Where(d => !d.IsType && d.Container == group.Key)
                               .OrderBy(d => d.File, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Line)
                               .ToList();

            foreach (var m in members)
                Console.WriteLine($"  {m.Line,6}  {Vis(m)}{m.Signature}");

            // Nested types are members too, but list them as pointers rather than expanding.
            var nested = decls.Where(d => d.IsType && d.Container == group.Key).ToList();
            foreach (var n in nested)
                Console.WriteLine($"  {n.Line,6}  {Vis(n)}{n.Signature}   // nested — outline {n.Fqn}");

            Console.WriteLine($"  // {members.Count} member(s), {nested.Count} nested type(s)"
                            + $", {parts.Count} part(s)");
            Console.WriteLine();
        }

        Console.WriteLine($"// {types.Select(t => t.Fqn).Distinct().Count()} type(s) — {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    // ---- tree --------------------------------------------------------------------------

    public static int Tree(Args a, Stopwatch sw)
    {
        var filter = a.At(0) ?? "";
        var decls = Load(a, out var set);

        var types = decls.Where(d => d.IsType)
            .Where(d => filter == "" || d.Fqn.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var memberCount = decls.Where(d => !d.IsType)
            .GroupBy(d => d.Container, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var proj in types.GroupBy(t => t.Project).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(proj.Key);
            foreach (var ns in proj.GroupBy(t => t.Container).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {(ns.Key.Length == 0 ? "<global>" : ns.Key)}");
                foreach (var t in ns.GroupBy(x => x.Fqn, StringComparer.Ordinal)
                                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var first = t.First();
                    var n = memberCount.GetValueOrDefault(t.Key, 0);
                    var partial = t.Count() > 1 ? $" ({t.Count()} parts)" : "";
                    Console.WriteLine($"    {first.KindWord,-10} {first.Name,-50} {n,4} members{partial}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"// {types.Select(t => t.Fqn).Distinct().Count()} type(s) in {types.Select(t => t.Project).Distinct().Count()} project(s) — {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    // ---- metrics -----------------------------------------------------------------------

    public static int Metrics(Args a, Stopwatch sw)
    {
        var top = a.Int("top", 30);
        var sort = a.Str("sort", "members");
        var decls = Load(a, out var set);

        var rows = new List<(string Fqn, string Kind, int Loc, int Members, int Methods, int MaxMethodLoc, int Ctors, int MaxParams, string File, int Parts)>();

        var membersBy = decls.Where(d => !d.IsType)
            .GroupBy(d => d.Container, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var g in decls.Where(d => d.IsType).GroupBy(d => d.Fqn, StringComparer.Ordinal))
        {
            var parts = g.ToList();
            var ms = membersBy.GetValueOrDefault(g.Key, []);
            var methods = ms.Where(m => m.Kind is DeclKind.Method).ToList();
            rows.Add((
                Fqn: g.Key,
                Kind: parts[0].KindWord,
                Loc: parts.Sum(p => p.Loc),
                Members: ms.Count,
                Methods: methods.Count,
                MaxMethodLoc: methods.Count == 0 ? 0 : methods.Max(m => m.Loc),
                Ctors: ms.Count(m => m.Kind is DeclKind.Ctor),
                MaxParams: ms.Count == 0 ? 0 : ms.Max(CountParams),
                File: Discovery.Rel(set.Root, parts[0].File),
                Parts: parts.Count));
        }

        IEnumerable<(string Fqn, string Kind, int Loc, int Members, int Methods, int MaxMethodLoc, int Ctors, int MaxParams, string File, int Parts)> ordered = sort.ToLowerInvariant() switch
        {
            "loc" => rows.OrderByDescending(r => r.Loc),
            "methods" => rows.OrderByDescending(r => r.Methods),
            "params" => rows.OrderByDescending(r => r.MaxParams),
            "members" => rows.OrderByDescending(r => r.Members),
            _ => throw new UserError($"unknown --sort '{sort}'. Known: members, loc, methods, params"),
        };

        Console.WriteLine($"{"LOC",6} {"MEMB",5} {"METH",5} {"MAXM",5} {"CTOR",4} {"PARM",4}  {"TYPE",-60} FILE");
        foreach (var r in ordered.Take(top))
            Console.WriteLine($"{r.Loc,6} {r.Members,5} {r.Methods,5} {r.MaxMethodLoc,5} {r.Ctors,4} {r.MaxParams,4}  "
                            + $"{r.Fqn,-60} {r.File}{(r.Parts > 1 ? $" (+{r.Parts - 1} parts)" : "")}");

        Console.WriteLine();
        Console.WriteLine($"// {rows.Count} type(s), sorted by {sort}, top {top} — {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("// LOC=source lines (all partial parts)  MEMB=members  METH=methods  "
                        + "MAXM=largest method LOC  PARM=largest param count");
        return 0;
    }

    static int CountParams(Decl d)
    {
        var open = d.Signature.IndexOf('(');
        var close = d.Signature.LastIndexOf(')');
        if (open < 0 || close <= open + 1) return 0;
        var inner = d.Signature[(open + 1)..close];
        return inner.Trim().Length == 0 ? 0 : inner.Split(',').Length;
    }

    /// <summary>Visibility prefix. Private members are the whole point of Tier 1, so make it explicit.</summary>
    static string Vis(Decl d) => d.Modifiers.Length == 0 ? "private " : d.Modifiers + " ";
}

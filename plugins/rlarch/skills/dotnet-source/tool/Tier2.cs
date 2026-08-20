// Tier2.cs — semantic commands over the in-memory Solution: find-usages / impls / calls / unused.
// Precondition is a RESTORE (for project.assets.json), not a build. See Workspace.cs.
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetSource;

static class Tier2
{
    /// <summary>
    /// Set by the `serve` daemon to hand these commands its already-warm Solution. When null
    /// (the normal, stateless case) the workspace is assembled from scratch.
    /// </summary>
    public static LoadedSolution? Warm { get; set; }

    static LoadedSolution Load(Args a, Stopwatch sw, bool projIsFilter = false)
    {
        if (Warm is not null) return Warm;

        var set = Discovery.Resolve(a, a.Has("include-generated"), ignoreProjTarget: projIsFilter);

        // Every Tier-2 answer is only as wide as the workspace. A single-project workspace cannot
        // see a caller in a sibling project, so "no usages" there means "none in this project" —
        // which reads identically to "none at all" unless we say so.
        if (set.Projects.Count == 1 && set.SolutionFile is null)
            Console.Error.WriteLine("// note: the workspace is a SINGLE project — references from other projects "
                                  + "cannot be seen. Pass --sln <path> for solution-wide results.");

        var loaded = WorkspaceBuilder.Build(set);
        if (a.Has("verbose"))
            foreach (var n in loaded.Notes) Console.Error.WriteLine($"// note: {n}");
        return loaded;
    }

    // ---- find-usages -------------------------------------------------------------------

    public static int FindUsages(Args a, Stopwatch sw)
    {
        var query = a.At(0) ?? throw new UserError("usage: find-usages <symbol>   (e.g. find-usages Thing.Bump)");
        var loaded = Load(a, sw);
        var solution = loaded.Solution;

        var found = Symbols.RequireAny(Symbols.Resolve(solution, query).GetAwaiter().GetResult(), query);

        if (Symbols.IsAmbiguous(found))
        {
            Console.WriteLine($"// '{query}' matches {found.Count} distinct symbols — showing all. "
                            + "Qualify it (Type.Member) to narrow.");
            foreach (var s in found) Console.WriteLine($"//   {Symbols.FullName(s)}   ({s.Kind})");
            Console.WriteLine();
        }

        var total = 0;
        foreach (var symbol in found)
        {
            Console.WriteLine($"{symbol.Kind} {Symbols.Signature(symbol)}");

            var refs = SymbolFinder.FindReferencesAsync(symbol, solution).GetAwaiter().GetResult().ToList();

            // Declaration sites. IL-based tools structurally cannot show these — it's a big part of
            // why a source-level find-usages exists at all.
            var decls = refs.SelectMany(r => r.Definition.Locations)
                            .Where(l => l.IsInSource)
                            .Select(l => (loc: l, kind: "declaration"))
                            .ToList();

            var uses = refs.SelectMany(r => r.Locations)
                           .Where(l => l.Location.IsInSource)
                           .ToList();

            var rows = new List<(string proj, string file, int line, int col, string kind, string text)>();

            foreach (var (loc, kind) in decls)
            {
                var span = loc.GetLineSpan();
                rows.Add((ProjectOf(solution, loc.SourceTree), span.Path, span.StartLinePosition.Line + 1,
                          span.StartLinePosition.Character + 1, kind, LineText(loc.SourceTree, span.StartLinePosition.Line)));
            }

            foreach (var u in uses)
            {
                var span = u.Location.GetLineSpan();
                var kind = u.IsImplicit ? "implicit" : u.IsCandidateLocation ? "candidate" : "reference";
                rows.Add((u.Document.Project.Name, span.Path, span.StartLinePosition.Line + 1,
                          span.StartLinePosition.Character + 1, kind,
                          LineText(u.Location.SourceTree, span.StartLinePosition.Line)));
            }

            rows = Dedupe(rows);

            foreach (var byProj in rows.GroupBy(r => r.proj).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {byProj.Key}");
                foreach (var byFile in byProj.GroupBy(r => r.file).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"    {Discovery.Rel(loaded.Set.Root, byFile.Key)}");
                    foreach (var r in byFile.OrderBy(r => r.line).ThenBy(r => r.col))
                        Console.WriteLine($"      {r.line,5},{r.col,-3} {r.kind,-11} {r.text.Trim()}");
                }
            }

            total += rows.Count;
            Console.WriteLine();
        }

        Console.WriteLine($"// {total} location(s) across {found.Count} symbol(s) — {sw.ElapsedMilliseconds} ms");
        return total > 0 ? 0 : 3;
    }

    /// <summary>
    /// FindReferencesAsync reports a property and its accessors as separate ReferencedSymbols, so a
    /// single `int TenantId { get; }` comes back twice — once for the property, once for get_TenantId —
    /// at different columns on the same line. Two rows for one declaration is just noise.
    ///
    /// So: declarations collapse per (file, line); references keep their column, because two genuine
    /// references really can share a line (`a.X > 0 && b.X != a.X`).
    /// </summary>
    static List<(string proj, string file, int line, int col, string kind, string text)> Dedupe(
        List<(string proj, string file, int line, int col, string kind, string text)> rows)
    {
        var decls = rows.Where(r => r.kind == "declaration")
                        .GroupBy(r => (r.file, r.line))
                        .Select(g => g.First());

        // Deliberately NOT filtered against declaration lines: `void Foo() => Foo();` is both a
        // declaration and a real reference, and dropping the recursion would be a lie.
        var others = rows.Where(r => r.kind != "declaration")
                         .GroupBy(r => (r.file, r.line, r.col))
                         .Select(g => g.First());

        return decls.Concat(others).ToList();
    }

    // ---- impls -------------------------------------------------------------------------

    public static int Impls(Args a, Stopwatch sw)
    {
        var query = a.At(0) ?? throw new UserError("usage: impls <TypeOrInterface> [--derived]");
        var loaded = Load(a, sw);
        var solution = loaded.Solution;

        var found = Symbols.RequireAny(
            Symbols.Resolve(solution, query, SymbolFilter.Type).GetAwaiter().GetResult(), query);

        var n = 0;
        foreach (var symbol in found.OfType<INamedTypeSymbol>())
        {
            Console.WriteLine($"{symbol.TypeKind.ToString().ToLowerInvariant()} {Symbols.FullName(symbol)}");

            IEnumerable<INamedTypeSymbol> results = a.Has("derived") || symbol.TypeKind != TypeKind.Interface
                ? SymbolFinder.FindDerivedClassesAsync(symbol, solution, transitive: true).GetAwaiter().GetResult()
                : SymbolFinder.FindImplementationsAsync(symbol, solution).GetAwaiter().GetResult().OfType<INamedTypeSymbol>();

            // Interfaces can also be extended by other interfaces — include those.
            if (symbol.TypeKind == TypeKind.Interface && !a.Has("derived"))
                results = results.Concat(SymbolFinder.FindDerivedInterfacesAsync(symbol, solution, transitive: true)
                                                     .GetAwaiter().GetResult());

            foreach (var r in results.DistinctBy(Symbols.FullName).OrderBy(Symbols.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var loc = r.Locations.FirstOrDefault(l => l.IsInSource);
                var where = loc is null ? "(from metadata)" : Where(loaded, loc);
                Console.WriteLine($"  {r.TypeKind.ToString().ToLowerInvariant(),-10} {Symbols.FullName(r),-70} {where}");
                n++;
            }
            Console.WriteLine();
        }

        Console.WriteLine($"// {n} result(s) — {sw.ElapsedMilliseconds} ms");
        return n > 0 ? 0 : 3;
    }

    // ---- calls -------------------------------------------------------------------------

    public static int Calls(Args a, Stopwatch sw)
    {
        var query = a.At(0) ?? throw new UserError("usage: calls <method> [--callers|--callees]");
        var callees = a.Has("callees");
        var loaded = Load(a, sw);
        var solution = loaded.Solution;

        var found = Symbols.RequireAny(
            Symbols.Resolve(solution, query, SymbolFilter.Member).GetAwaiter().GetResult(), query);

        var n = 0;
        foreach (var symbol in found)
        {
            Console.WriteLine($"{(callees ? "callees of" : "callers of")} {Symbols.Signature(symbol)}");
            n += callees ? Callees(loaded, symbol) : Callers(loaded, symbol);
            Console.WriteLine();
        }

        Console.WriteLine($"// {n} result(s) — {sw.ElapsedMilliseconds} ms");
        return n > 0 ? 0 : 3;
    }

    static int Callers(LoadedSolution loaded, ISymbol symbol)
    {
        var callers = SymbolFinder.FindCallersAsync(symbol, loaded.Solution).GetAwaiter().GetResult().ToList();
        var n = 0;
        foreach (var c in callers.OrderBy(c => Symbols.FullName(c.CallingSymbol), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var loc in c.Locations.Where(l => l.IsInSource))
            {
                Console.WriteLine($"  {Symbols.Signature(c.CallingSymbol),-70} {Where(loaded, loc)}"
                                + (c.IsDirect ? "" : "   (indirect)"));
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Callees have no SymbolFinder equivalent — walk the method's body and ask the semantic model
    /// what each invocation binds to.
    /// </summary>
    static int Callees(LoadedSolution loaded, ISymbol symbol)
    {
        var n = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            var doc = loaded.Solution.GetDocument(node.SyntaxTree);
            if (doc is null) continue;
            var model = doc.GetSemanticModelAsync().GetAwaiter().GetResult();
            if (model is null) continue;

            foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var target = model.GetSymbolInfo(inv).Symbol;
                if (target is null) continue;
                var sig = Symbols.Signature(target);
                if (!seen.Add(sig)) continue;

                var span = inv.GetLocation().GetLineSpan();
                var origin = target.Locations.FirstOrDefault(l => l.IsInSource) is { } src
                    ? Where(loaded, src) : "(external)";
                Console.WriteLine($"  {sig,-70} {origin}   // called at {Discovery.Rel(loaded.Set.Root, span.Path)}:{span.StartLinePosition.Line + 1}");
                n++;
            }
        }
        return n;
    }

    // ---- unused ------------------------------------------------------------------------

    public static int Unused(Args a, Stopwatch sw)
    {
        // projIsFilter: --proj scopes WHICH declarations we report, never how much of the solution
        // we search for their usages.
        var loaded = Load(a, sw, projIsFilter: true);
        var solution = loaded.Solution;
        var typeFilter = a.Str("type");
        var projFilter = a.Str("proj");
        var includePublic = a.Has("include-public");

        if (typeFilter is null && projFilter is null)
            Console.Error.WriteLine("// note: scanning the WHOLE solution. This is the slow one — "
                                  + "scope it with --proj <csproj> or --type <Ns.Type>.");

        var projects = solution.Projects.AsEnumerable();
        if (projFilter is not null)
        {
            var full = Path.GetFullPath(projFilter);
            projects = projects.Where(p => string.Equals(p.FilePath, full, StringComparison.OrdinalIgnoreCase)
                                        || p.Name.Equals(Path.GetFileNameWithoutExtension(full), StringComparison.OrdinalIgnoreCase));
            if (!projects.Any()) throw new UserError($"--proj '{projFilter}' is not in the solution");
        }

        var kinds = a.Csv("kind");
        var n = 0;

        // A partial type declares the SAME symbol in several files, so without this it gets
        // reported once per part. Doc-comment id is the stable identity.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var doc in project.Documents)
            {
                // Document.GetSemanticModelAsync(), NOT compilation.GetSemanticModel(tree).
                // Both give a working model, but only the document-level API yields symbols that
                // SymbolFinder can map back onto the Solution's project graph. With the manual
                // compilation the reference search silently only saw the DECLARING project — so a
                // public member used from another project looked unused. That's a false positive
                // that invites someone to delete live code, which is the worst thing this command
                // could do.
                var model = doc.GetSemanticModelAsync().GetAwaiter().GetResult();
                var root = doc.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (model is null || root is null) continue;

                foreach (var node in root.DescendantNodes().Where(IsDeclaration))
                {
                    var symbol = model.GetDeclaredSymbol(node);
                    if (symbol is null) continue;
                    if (!reported.Add(symbol.GetDocumentationCommentId() ?? Symbols.Signature(symbol))) continue;
                    if (!Wanted(symbol, kinds)) continue;
                    if (typeFilter is not null &&
                        !Symbols.SuffixMatches(Symbols.FullName(symbol.ContainingType ?? (symbol as INamedTypeSymbol)!), typeFilter))
                        continue;

                    // Public/protected members can be called from outside this solution, so
                    // "unused" is unprovable here — excluded by default. --include-public opts in:
                    // for a leaf app or an internal service nothing else consumes, the whole
                    // solution IS the world and public dead code is exactly what you're hunting.
                    var isPublicApi = symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected
                                   or Accessibility.ProtectedOrInternal;
                    if (isPublicApi && !includePublic) continue;
                    if (IsExempt(symbol)) continue;

                    var refs = SymbolFinder.FindReferencesAsync(symbol, solution).GetAwaiter().GetResult();
                    var uses = refs.SelectMany(r => r.Locations).Count();
                    if (uses > 0) continue;

                    var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    var vis = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
                    Console.WriteLine($"  {vis,-9} {symbol.Kind,-9} {Symbols.Signature(symbol),-70} "
                                    + (loc is null ? "" : Where(loaded, loc)));
                    n++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"// {n} unused declaration(s) — {sw.ElapsedMilliseconds} ms");
        Console.WriteLine(includePublic
            ? "// --include-public: PUBLIC declarations are included. Nothing in THIS SOLUTION references\n"
            + "//   them — which only means dead code if the solution is the whole world. If this is a\n"
            + "//   library, a public member with no local references is its API, not garbage. Anything\n"
            + "//   reached reflectively (DI, EF, serialization, xunit) or via an attribute or override is\n"
            + "//   still skipped, but a reference search cannot see every caller. Verify before deleting."
            : "// only non-public declarations are reported: a public member may be used from outside this\n"
            + "//   solution, so 'unused' cannot be concluded here. Pass --include-public to include them\n"
            + "//   (right for a leaf app; misleading for a library).");
        return 0;
    }

    /// <summary>
    /// Nodes that GetDeclaredSymbol actually answers for.
    ///
    /// Note FieldDeclarationSyntax is NOT here: `private int a, b;` is ONE FieldDeclarationSyntax
    /// declaring TWO symbols, so GetDeclaredSymbol returns null for it. The symbol hangs off each
    /// VariableDeclaratorSyntax. Asking the wrong node doesn't error — it silently returns null, so
    /// fields would just never be reported as unused.
    /// </summary>
    static bool IsDeclaration(SyntaxNode n) => n is MethodDeclarationSyntax or PropertyDeclarationSyntax
        or EventDeclarationSyntax or ClassDeclarationSyntax or StructDeclarationSyntax
        or InterfaceDeclarationSyntax or RecordDeclarationSyntax or EnumDeclarationSyntax
        || n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax };

    static bool Wanted(ISymbol s, HashSet<string> kinds)
    {
        if (kinds.Count == 0) return true;
        var word = s.Kind switch
        {
            SymbolKind.Method => "method",
            SymbolKind.Property => "prop",
            SymbolKind.Field => "field",
            SymbolKind.Event => "event",
            SymbolKind.NamedType => "type",
            _ => "?",
        };
        return kinds.Contains(word) || (word == "prop" && kinds.Contains("property"));
    }

    /// <summary>
    /// Declarations whose users a reference search structurally can't see, so "unused" would be a
    /// false positive: compiler-generated members, overrides (called through the base), and anything
    /// carrying an attribute (DI, EF, serialization, xunit [Fact] — all invoked reflectively).
    /// </summary>
    static bool IsExempt(ISymbol s) =>
        s.IsImplicitlyDeclared
        || s.IsOverride
        || s.GetAttributes().Length > 0
        || s is IMethodSymbol { MethodKind: not MethodKind.Ordinary };

    // ---- shared ------------------------------------------------------------------------

    static string Where(LoadedSolution loaded, Location loc)
    {
        var span = loc.GetLineSpan();
        return $"{Discovery.Rel(loaded.Set.Root, span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    static string ProjectOf(Solution solution, SyntaxTree? tree) =>
        tree is null ? "?" : solution.GetDocument(tree)?.Project.Name ?? "?";

    static string LineText(SyntaxTree? tree, int line)
    {
        if (tree is null) return "";
        try
        {
            var text = tree.GetText();
            return line < text.Lines.Count ? text.Lines[line].ToString() : "";
        }
        catch { return ""; }
    }
}

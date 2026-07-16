// Symbols.cs — resolve a CLI <symbol> string to concrete ISymbol(s).
//
// This, not SymbolFinder, is the hard part of find-usages. SymbolFinder is a one-line call once
// you HAVE an ISymbol; getting there from a string like "Thing.Bump" is where the usability lives:
//   * a name can be declared in several projects, and each project has its own Compilation, so
//     "the" symbol isn't unique — we search every project and dedupe by documentation-comment id
//     (the only cross-compilation stable identity).
//   * GetTypeByMetadataName would need backtick arity (`List`1`) and nested `+` separators, and
//     returns null on ambiguity — too brittle for a human-typed argument. SymbolFinder's
//     declaration search is name-based and does the right thing.
//   * matching must be at DOTTED SEGMENT boundaries or `Bump` would match `PumpBump`.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotnetSource;

static class Symbols
{
    /// <summary>Ns.Type.Member for members, Ns.Type for types. No generics — arity is matched separately.</summary>
    static readonly SymbolDisplayFormat FullFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    public static string FullName(ISymbol s) => s.ToDisplayString(FullFormat);

    /// <summary>Human-readable signature for disambiguation output.</summary>
    public static string Signature(ISymbol s) => s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    /// <summary>
    /// Normalize what a user (or an agent copying from docs) might paste:
    /// `M:Ns.Type.Member(System.Int32)` / `T:Ns.Type` / `Ns.Type.Member(int)` -> `Ns.Type.Member`.
    /// </summary>
    public static string Normalize(string raw)
    {
        var s = raw.Trim();
        if (s.Length > 2 && s[1] == ':' && "MTPFE".Contains(char.ToUpperInvariant(s[0]))) s = s[2..];
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        var generic = s.IndexOf('<');
        if (generic >= 0) s = s[..generic];
        return s.Trim().Trim('.');
    }

    /// <summary>
    /// Every source-declared symbol whose full name ends with <paramref name="query"/> at a dotted
    /// boundary. Searched across all projects, deduped by doc-comment id.
    /// </summary>
    public static async Task<List<ISymbol>> Resolve(Solution solution, string query, SymbolFilter filter = SymbolFilter.TypeAndMember)
    {
        var q = Normalize(query);
        if (q.Length == 0) throw new UserError("empty <symbol>");

        var lastDot = q.LastIndexOf('.');
        var name = lastDot < 0 ? q : q[(lastDot + 1)..];

        var byId = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            IEnumerable<ISymbol> found;
            try { found = await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: true, filter: filter); }
            catch { continue; }

            foreach (var s in found)
            {
                if (!SuffixMatches(FullName(s), q)) continue;
                // Doc-comment id is the stable identity across compilations: the same source symbol
                // reached via project A and project B must collapse to one result.
                var id = s.GetDocumentationCommentId() ?? FullName(s) + "|" + s.Kind;
                byId.TryAdd(id, s);
            }
        }
        return byId.Values.ToList();
    }

    /// <summary>
    /// `full` ends with `query` on a dotted-segment boundary, case-insensitively.
    /// "Ns.Type.Bump" matches "Bump", "Type.Bump", "Ns.Type.Bump" — but NOT "ump" or "PumpBump".
    /// </summary>
    public static bool SuffixMatches(string full, string query)
    {
        if (full.Equals(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!full.EndsWith(query, StringComparison.OrdinalIgnoreCase)) return false;
        var boundary = full.Length - query.Length - 1;
        return boundary >= 0 && full[boundary] == '.';
    }

    /// <summary>
    /// Pick one symbol, or explain the ambiguity instead of guessing. Overloads are kept together
    /// (they share a full name) — callers that can handle a set get them all.
    /// </summary>
    public static List<ISymbol> RequireAny(List<ISymbol> found, string query)
    {
        if (found.Count != 0) return found;
        throw new UserError(
            $"no source declaration matches '{query}'.\n"
          + "  <symbol> is matched at dotted-segment boundaries: Member | Type.Member | Ns.Type.Member | Ns.Type.\n"
          + "  It must be DECLARED IN THIS SOLUTION'S SOURCE. For a symbol declared in a NuGet package or the\n"
          + "  framework (e.g. IsDevelopment), use the dotnet-reflect skill instead — it reads compiled IL.\n"
          + "  Try `search " + query.Split('.').Last() + "` to see what is declared here.");
    }

    /// <summary>Group distinct full names so we can warn when a query spans unrelated symbols.</summary>
    public static bool IsAmbiguous(List<ISymbol> found) =>
        found.Select(FullName).Distinct(StringComparer.Ordinal).Count() > 1;
}

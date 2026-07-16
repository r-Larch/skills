// Discovery.cs — locate the solution, its project set, and each project's .cs files.
//
// THE SOLUTION FILE IS AUTHORITATIVE when one is present. This is not a stylistic choice:
// a bare `**/*.csproj` glob walks into nested git worktrees and vendored solution copies and
// ingests the whole codebase several times over. Measured on the Nomos solution:
//     95 .csproj on disk   vs   21 .csproj listed in Nomos.slnx   (73 of them under .claude/worktrees/)
// A 4.5x duplication would mean duplicate FQNs, garbage metrics, duplicated find-usages hits,
// and 4.5x the parse cost — all silently. So: solution file first, glob only as a last resort,
// and even then with hard directory exclusions.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotnetSource;

sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string CsprojPath { get; init; }
    public required string Dir { get; init; }

    public string Tfm { get; set; } = "";
    public string[] DefineConstants { get; set; } = [];
    public string LangVersion { get; set; } = "";
    public bool AllowUnsafe { get; set; }
    public bool NullableEnable { get; set; }
    public string OutputType { get; set; } = "Library";
    public string Sdk { get; set; } = "Microsoft.NET.Sdk";
    public bool ImplicitUsings { get; set; }
    /// <summary>&lt;Using Include="X" Alias="Y" Static="true"/&gt; items declared in the csproj.</summary>
    public List<(string Ns, string? Alias, bool Static)> Usings { get; } = [];

    public List<string> Files { get; } = [];
    public List<string> ProjectRefs { get; } = [];   // absolute .csproj paths

    public override string ToString() => Name;
}

sealed class SolutionSet
{
    public required string Root { get; init; }
    public string? SolutionFile { get; init; }
    public List<ProjectInfo> Projects { get; init; } = [];
    public List<string> Notes { get; } = [];
    /// <summary>Part of the index key: the two profiles see different file sets.</summary>
    public bool IncludeGenerated { get; set; }

    public int FileCount => Projects.Sum(p => p.Files.Count);

    /// <summary>Nearest-ancestor lookup: which project owns this .cs file.</summary>
    public ProjectInfo? OwnerOf(string file) =>
        Projects.FirstOrDefault(p => p.Files.Contains(file, StringComparer.OrdinalIgnoreCase));
}

static class Discovery
{
    // Directory names never walked into. `.claude` is the important one here (it holds
    // worktrees = full solution copies); bin/obj keep build output and generated sources out.
    static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", ".git", ".vs", ".idea", ".claude", ".svn", "node_modules", "TestResults", "artifacts", ".vscode" };

    public static bool IsExcludedDir(string name) =>
        ExcludedDirs.Contains(name) || name.StartsWith('.') && name is not "." and not "..";

    /// <summary>True if any segment of the path is an excluded directory (used by the file watcher).</summary>
    public static bool IsExcludedPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(IsExcludedDir);

    /// <summary>
    /// Just the root, without enumerating any files. The daemon probe runs on every Tier-2
    /// command, so it must not pay for a full discovery pass just to compute a pipe name.
    /// </summary>
    public static string ResolveRootOnly(Args a)
    {
        if (a.Str("proj") is { } p) return Path.GetDirectoryName(Path.GetFullPath(p))!;
        if (a.Str("sln") is { } s) return Path.GetDirectoryName(Path.GetFullPath(s))!;
        if (a.Str("root") is { } r) return Path.GetFullPath(r);
        var found = FindSolutionUpwards(Environment.CurrentDirectory);
        return found is not null ? Path.GetDirectoryName(found)! : Environment.CurrentDirectory;
    }

    static bool IsGenerated(string path)
    {
        var n = Path.GetFileName(path);
        return n.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the target from --sln / --proj / --root, else walk up from cwd for a *.slnx|*.sln.
    /// </summary>
    public static SolutionSet Resolve(Args a, bool includeGenerated = false)
    {
        var set = ResolveCore(a, includeGenerated);
        set.IncludeGenerated = includeGenerated;
        return set;
    }

    static SolutionSet ResolveCore(Args a, bool includeGenerated)
    {
        var sln = a.Str("sln");
        var proj = a.Str("proj");
        var root = a.Str("root");

        if (proj is not null)
        {
            var full = Path.GetFullPath(proj);
            if (!File.Exists(full)) throw new UserError($"--proj not found: {full}");
            var set = new SolutionSet { Root = Path.GetDirectoryName(full)!, SolutionFile = null };
            set.Projects.Add(NewProject(full));
            Populate(set, includeGenerated);
            return set;
        }

        if (sln is null && root is null)
        {
            sln = FindSolutionUpwards(Environment.CurrentDirectory);
            if (sln is null) root = Environment.CurrentDirectory;
        }

        if (sln is not null)
        {
            var full = Path.GetFullPath(sln);
            if (!File.Exists(full)) throw new UserError($"--sln not found: {full}");
            var dir = Path.GetDirectoryName(full)!;
            var set = new SolutionSet { Root = dir, SolutionFile = full };
            foreach (var p in ParseSolution(full))
            {
                if (!File.Exists(p)) { set.Notes.Add($"listed in solution but missing on disk: {Rel(dir, p)}"); continue; }
                set.Projects.Add(NewProject(p));
            }
            Populate(set, includeGenerated);
            NoteUnlisted(set);
            return set;
        }

        // Last resort: glob. Only reached when there is no solution file at all.
        var r = Path.GetFullPath(root!);
        if (!Directory.Exists(r)) throw new UserError($"--root not found: {r}");
        var globbed = new SolutionSet { Root = r, SolutionFile = null };
        foreach (var c in EnumerateFiles(r, "*.csproj")) globbed.Projects.Add(NewProject(c));
        globbed.Notes.Add($"no .slnx/.sln found under {r} — using a *.csproj glob ({globbed.Projects.Count} projects). "
                        + "Pass --sln for an authoritative project set.");
        Populate(globbed, includeGenerated);
        return globbed;
    }

    static ProjectInfo NewProject(string csproj)
    {
        var full = Path.GetFullPath(csproj);
        var p = new ProjectInfo
        {
            Name = Path.GetFileNameWithoutExtension(full),
            CsprojPath = full,
            Dir = Path.GetDirectoryName(full)!,
        };
        ParseCsproj(p);
        return p;
    }

    static void Populate(SolutionSet set, bool includeGenerated)
    {
        // Assign each .cs to its NEAREST ancestor project. Shorter dirs first so that a nested
        // project (longer dir) overwrites its parent's claim on shared files.
        var owner = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in set.Projects.OrderBy(p => p.Dir.Length))
            foreach (var f in EnumerateFiles(p.Dir, "*.cs"))
            {
                if (!includeGenerated && IsGenerated(f)) continue;
                owner[f] = p;
            }

        foreach (var (file, p) in owner) p.Files.Add(file);
        foreach (var p in set.Projects) p.Files.Sort(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Projects on disk but absent from the solution — invisible by design; say so once.</summary>
    static void NoteUnlisted(SolutionSet set)
    {
        var listed = set.Projects.Select(p => p.CsprojPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onDisk = EnumerateFiles(set.Root, "*.csproj").Where(c => !listed.Contains(c)).ToList();
        if (onDisk.Count > 0)
            set.Notes.Add($"{onDisk.Count} .csproj on disk are not in the solution and are therefore NOT scanned: "
                        + string.Join(", ", onDisk.Take(5).Select(c => Rel(set.Root, c)))
                        + (onDisk.Count > 5 ? " …" : "") + ". Use --root to scan them anyway.");
    }

    /// <summary>Recursive file walk that skips excluded directories instead of filtering after the fact.</summary>
    public static IEnumerable<string> EnumerateFiles(string dir, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(d, pattern); } catch { continue; }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in subs)
                if (!IsExcludedDir(Path.GetFileName(s))) stack.Push(s);
        }
    }

    static string? FindSolutionUpwards(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        {
            var slnx = d.GetFiles("*.slnx").FirstOrDefault();
            if (slnx is not null) return slnx.FullName;
            var sln = d.GetFiles("*.sln").FirstOrDefault();
            if (sln is not null) return sln.FullName;
        }
        return null;
    }

    // ---- solution parsing -------------------------------------------------------------

    public static IEnumerable<string> ParseSolution(string path) =>
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? ParseSlnx(path) : ParseSln(path);

    /// <summary>
    /// .slnx is plain XML. &lt;Project Path="…"/&gt; appears both nested inside &lt;Folder&gt; and directly
    /// under &lt;Solution&gt;, so a descendant scan is right. &lt;File&gt; entries (Solution Items) are a
    /// different element name and are correctly ignored.
    /// </summary>
    static IEnumerable<string> ParseSlnx(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception e) { throw new UserError($"could not parse {path}: {e.Message}"); }

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Project"))
        {
            var rel = (string?)el.Attribute("Path");
            if (string.IsNullOrWhiteSpace(rel)) continue;
            if (!rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;   // skip .vbproj/.fsproj
            yield return Normalize(dir, rel);
        }
    }

    /// <summary>Classic .sln: `Project("{type}") = "Name", "rel\path.csproj", "{guid}"`.</summary>
    static IEnumerable<string> ParseSln(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var rx = new Regex("""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"([^"]+)"\s*,""",
                           RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(File.ReadAllText(path)))
        {
            var rel = m.Groups[1].Value;
            if (!rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;   // skips solution folders too
            yield return Normalize(dir, rel);
        }
    }

    // Solution files use '\' or '/' regardless of host OS.
    static string Normalize(string baseDir, string rel) =>
        Path.GetFullPath(Path.Combine(baseDir, rel.Replace('\\', Path.DirectorySeparatorChar)
                                                  .Replace('/', Path.DirectorySeparatorChar)));

    // ---- csproj parsing ---------------------------------------------------------------

    /// <summary>
    /// Best-effort, non-evaluating csproj read: we want ParseOptions inputs, not an MSBuild
    /// evaluation. Properties referencing $(…) are dropped rather than guessed at.
    /// </summary>
    static void ParseCsproj(ProjectInfo p)
    {
        XDocument doc;
        try { doc = XDocument.Load(p.CsprojPath); } catch { ApplyDefaults(p); return; }

        string? Prop(string name) => doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

        // TFM: single, else first of the plural form (prefer a net10.x if present).
        var tfm = Prop("TargetFramework");
        if (string.IsNullOrWhiteSpace(tfm))
        {
            var many = (Prop("TargetFrameworks") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            tfm = many.FirstOrDefault(t => t.StartsWith("net10.", StringComparison.OrdinalIgnoreCase)) ?? many.FirstOrDefault();
        }
        // Fall back to a Directory.Build.props up the tree (common for centrally-set TFMs).
        tfm ??= PropFromDirectoryBuildProps(p.Dir, "TargetFramework");
        p.Tfm = tfm ?? "net10.0";

        p.OutputType = Prop("OutputType") ?? "Library";
        p.AllowUnsafe = string.Equals(Prop("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase);
        p.NullableEnable = string.Equals(Prop("Nullable"), "enable", StringComparison.OrdinalIgnoreCase);
        p.LangVersion = Prop("LangVersion") ?? "";
        p.Sdk = (string?)doc.Root?.Attribute("Sdk") ?? "Microsoft.NET.Sdk";
        p.ImplicitUsings = (Prop("ImplicitUsings") ?? "") is "enable" or "true";

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Using"))
        {
            var inc = (string?)el.Attribute("Include");
            if (string.IsNullOrWhiteSpace(inc)) continue;
            p.Usings.Add((inc.Trim(), (string?)el.Attribute("Alias"),
                          string.Equals((string?)el.Attribute("Static"), "true", StringComparison.OrdinalIgnoreCase)));
        }

        // Preprocessor symbols. Getting these wrong means every reference inside an `#if DEBUG`
        // block is invisible to find-usages — silently. Default Debug config implies DEBUG;TRACE.
        var defines = new List<string> { "DEBUG", "TRACE" };
        foreach (var raw in (Prop("DefineConstants") ?? "")
                 .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!raw.Contains("$(", StringComparison.Ordinal)) defines.Add(raw);
        p.DefineConstants = defines.Distinct(StringComparer.Ordinal).ToArray();

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var inc = (string?)el.Attribute("Include");
            if (string.IsNullOrWhiteSpace(inc)) continue;
            p.ProjectRefs.Add(Normalize(p.Dir, inc));
        }
    }

    static void ApplyDefaults(ProjectInfo p)
    {
        p.Tfm = "net10.0";
        p.DefineConstants = ["DEBUG", "TRACE"];
    }

    static string? PropFromDirectoryBuildProps(string startDir, string name)
    {
        for (var d = new DirectoryInfo(startDir); d is not null; d = d.Parent)
        {
            var f = d.GetFiles("Directory.Build.props").FirstOrDefault();
            if (f is null) continue;
            try
            {
                var v = XDocument.Load(f.FullName).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(v) && !v.Contains("$(", StringComparison.Ordinal)) return v;
            }
            catch { /* unreadable props file: fall through */ }
        }
        return null;
    }

    public static string Rel(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); } catch { return path; }
    }
}

/// <summary>A message meant for the user, printed without a stack trace.</summary>
sealed class UserError(string message) : Exception(message);

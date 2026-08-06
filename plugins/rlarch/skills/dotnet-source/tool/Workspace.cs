// Workspace.cs — assemble a Roslyn Solution in memory. NO MSBuild.
//
// MSBuildWorkspace.OpenSolutionAsync runs a design-time build of every project: slow at scale,
// fragile about SDK resolution, and re-paid on every invocation. Instead we build the Solution
// ourselves from things already on disk.
//
// References come from `obj/project.assets.json`, NOT from `bin/`. This is a correctness rule,
// not a preference:
//   * assets.json lists only *package* compile paths (into the NuGet cache). It never contains
//     the project's own output or a sibling project's dll.
//   * If project P is in the workspace as SOURCE and you also add P/bin/**/P.dll as a
//     MetadataReference, every type in P is declared twice (source + metadata) -> ambiguous
//     symbols and SymbolFinder confusion.
//   * assets.json only needs `dotnet restore`, never a compile. So Tier 2's precondition is a
//     restore, not a green build.
// Sibling projects are wired as ProjectReference source edges instead (targets entries of
// "type": "project" are deliberately skipped).

using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using RoslynProjectInfo = Microsoft.CodeAnalysis.ProjectInfo;

namespace DotnetSource;

sealed class LoadedSolution
{
    public required AdhocWorkspace Workspace { get; init; }
    public required Solution Solution { get; init; }
    public required SolutionSet Set { get; init; }
    public List<string> Notes { get; } = [];
    public Dictionary<string, ProjectId> ByCsproj { get; } = new(StringComparer.OrdinalIgnoreCase);
}

static class WorkspaceBuilder
{
    public static LoadedSolution Build(SolutionSet set, bool autoRestore = true)
    {
        // Force-load the C# workspace assembly so MEF's default host discovers its language
        // services; without them SymbolFinder can't operate on C# documents.
        _ = typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions);
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        var ws = new AdhocWorkspace(host);

        var notes = new List<string>();

        // Framework refs are shared across every project — resolve once.
        var frameworkDlls = FrameworkDlls();

        var ids = set.Projects.ToDictionary(
            p => p.CsprojPath,
            p => ProjectId.CreateNewId(p.Name),
            StringComparer.OrdinalIgnoreCase);

        var byPath = set.Projects.ToDictionary(p => p.CsprojPath, p => p, StringComparer.OrdinalIgnoreCase);

        var infos = new List<RoslynProjectInfo>();
        foreach (var p in set.Projects)
        {
            var (packageDlls, note) = PackageRefs(p, autoRestore);
            if (note is not null) notes.Add(note);

            // Package dlls win over the shared framework copy of the same file name; never add a
            // dll twice under two paths (duplicate assembly identity -> ambiguity).
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in packageDlls) byName.TryAdd(Path.GetFileName(d), d);
            foreach (var d in frameworkDlls) byName.TryAdd(Path.GetFileName(d), d);

            var metadata = byName.Values
                .Select(d => { try { return (MetadataReference)MetadataReference.CreateFromFile(d); } catch { return null; } })
                .Where(r => r is not null)!
                .Cast<MetadataReference>()
                .ToList();

            var parse = new CSharpParseOptions(LanguageVersion.Preview)
                .WithPreprocessorSymbols(p.DefineConstants)
                .WithDocumentationMode(DocumentationMode.Parse);

            var options = new CSharpCompilationOptions(
                    p.OutputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                        ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(p.AllowUnsafe)
                .WithNullableContextOptions(p.NullableEnable ? NullableContextOptions.Enable : NullableContextOptions.Disable)
                // We never emit; suppress the noise of an unsigned/entrypoint-less compile.
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                {
                    ["CS5001"] = ReportDiagnostic.Suppress,   // no Main
                });

            var docs = p.Files.Select(f => DocumentInfo.Create(
                DocumentId.CreateNewId(ids[p.CsprojPath], f),
                Path.GetFileName(f),
                loader: new FileTextLoaderUtf8(f),
                filePath: f)).ToList();

            // .razor components are compiled by the Razor source generator, which runs in-memory and
            // emits no .cs to disk — so a source-only reader cannot see those types and references to
            // them won't resolve. Bounded and worth naming rather than leaving as a mystery.
            var razor = CountRazor(p.Dir);
            if (razor > 0)
                notes.Add($"{p.Name}: {razor} .razor component(s) are not compiled (the Razor source generator "
                        + "emits no .cs to disk). References to those component TYPES won't resolve; everything else does.");

            // Implicit usings. Tier 1 rightly hides generated code, but Tier 2 needs it: without
            // the global usings, `Task<>`, `List<>`, `CancellationToken` and friends don't bind and
            // the compilation drowns in CS0246 — which would make find-usages silently miss
            // references. Measured on Nomos: omitting this produced 10,392 CS0246.
            var (globalUsings, guNote) = GlobalUsings(p);
            if (guNote is not null) notes.Add(guNote);
            if (globalUsings is not null)
                docs.Add(DocumentInfo.Create(
                    DocumentId.CreateNewId(ids[p.CsprojPath], "GlobalUsings"),
                    "__GlobalUsings.g.cs",
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(globalUsings, System.Text.Encoding.UTF8), VersionStamp.Create())),
                    filePath: Path.Combine(p.Dir, "__GlobalUsings.g.cs"),
                    isGenerated: true));

            // TRANSITIVE closure, not just the direct refs. MSBuild flows ProjectReference
            // transitively to the compiler; Roslyn's ProjectReference does not. Nomos.Web
            // references Nomos.Core, which references LarchSys.Ai — and Nomos.Web uses
            // LarchSys.Ai's `Citation` without referencing it directly. Wiring only direct refs
            // left 317 CS0246 (all of them Nomos's own types) and would have made find-usages
            // silently miss every cross-project reference through an intermediate project.
            var closure = TransitiveRefs(p, byPath);

            var projRefs = closure
                .Where(ids.ContainsKey)                             // refs outside the solution: skip
                .Select(r => new ProjectReference(ids[r]))
                .ToList();

            foreach (var r in closure.Where(r => !ids.ContainsKey(r)))
                notes.Add($"{p.Name}: <ProjectReference> outside the solution is not loaded: {Discovery.Rel(set.Root, r)}");

            infos.Add(RoslynProjectInfo.Create(
                ids[p.CsprojPath],
                VersionStamp.Create(),
                name: p.Name,
                assemblyName: p.Name,
                language: LanguageNames.CSharp,
                filePath: p.CsprojPath,
                compilationOptions: options,
                parseOptions: parse,
                documents: docs,
                projectReferences: projRefs,
                metadataReferences: metadata));
        }

        var solution = ws.CurrentSolution;
        foreach (var i in infos) solution = solution.AddProject(i);
        ws.TryApplyChanges(solution);

        var result = new LoadedSolution { Workspace = ws, Solution = ws.CurrentSolution, Set = set };
        result.Notes.AddRange(notes);
        foreach (var (k, v) in ids) result.ByCsproj[k] = v;
        return result;
    }

    static int CountRazor(string dir)
    {
        try { return Discovery.EnumerateFiles(dir, "*.razor").Count(); } catch { return 0; }
    }

    /// <summary>
    /// Every project reachable through &lt;ProjectReference&gt;, transitively. Cycle-safe.
    /// (A ProjectReference marked PrivateAssets=all wouldn't flow in MSBuild; we include it anyway.
    /// For a navigation tool, over-including costs a little memory, whereas under-including makes
    /// find-usages silently miss real references.)
    /// </summary>
    static List<string> TransitiveRefs(ProjectInfo p, Dictionary<string, ProjectInfo> byPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(p.ProjectRefs);
        while (stack.Count > 0)
        {
            var r = stack.Pop();
            if (!seen.Add(r)) continue;
            if (byPath.TryGetValue(r, out var rp))
                foreach (var t in rp.ProjectRefs)
                    if (!seen.Contains(t)) stack.Push(t);
        }
        seen.Remove(p.CsprojPath);   // a cycle must not make a project reference itself
        return [.. seen];
    }

    // ---- implicit / global usings -------------------------------------------------------

    /// <summary>
    /// The project's global usings, as C# source.
    ///
    /// Prefer the REAL generated obj/**/&lt;Proj&gt;.GlobalUsings.g.cs, because it captures usings
    /// contributed by *packages* via their build props — e.g. xunit's `global using Xunit;` or
    /// Sentry's `global using Sentry;`. Those appear nowhere in the csproj, so synthesising from
    /// the csproj alone would silently miss them and every `[Fact]` would fail to bind.
    ///
    /// That file is written at BUILD time though, so fall back to synthesising the SDK's documented
    /// implicit-using set plus the csproj's own &lt;Using&gt; items. That keeps a never-built project
    /// working (degraded: package-contributed usings are missing).
    /// </summary>
    static (string? source, string? note) GlobalUsings(ProjectInfo p)
    {
        var real = FindGeneratedGlobalUsings(p);
        if (real is not null)
        {
            try { return (File.ReadAllText(real), null); } catch { /* fall through to synthesis */ }
        }

        if (!p.ImplicitUsings && p.Usings.Count == 0) return (null, null);

        var sb = new System.Text.StringBuilder("// <synthesized by dotnet-source>\n");
        foreach (var ns in ImplicitUsingsFor(p.Sdk, p.ImplicitUsings)) sb.Append($"global using {ns};\n");
        foreach (var (ns, alias, isStatic) in p.Usings)
            sb.Append(alias is not null ? $"global using {alias} = {ns};\n"
                    : isStatic ? $"global using static {ns};\n"
                    : $"global using {ns};\n");

        return (sb.ToString(),
            $"{p.Name}: no generated GlobalUsings found (project never built) — synthesized the SDK set. "
          + "Usings contributed by packages (e.g. Xunit) are missing; `dotnet build` once for full fidelity.");
    }

    static string? FindGeneratedGlobalUsings(ProjectInfo p)
    {
        var objDir = Path.Combine(p.Dir, "obj");
        if (!Directory.Exists(objDir)) return null;
        string[] found;
        try { found = Directory.GetFiles(objDir, "*.GlobalUsings.g.cs", SearchOption.AllDirectories); }
        catch { return null; }
        if (found.Length == 0) return null;

        // Debug and Release both exist in a built tree — take exactly one, or the same global using
        // would be declared twice.
        var sep = Path.DirectorySeparatorChar;
        return found
            .OrderByDescending(f => f.Contains($"{sep}{p.Tfm}{sep}", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Contains($"{sep}Debug{sep}", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    /// <summary>The SDK's documented implicit-using sets (only used when nothing is generated).</summary>
    static IEnumerable<string> ImplicitUsingsFor(string sdk, bool enabled)
    {
        if (!enabled) yield break;

        yield return "System";
        yield return "System.Collections.Generic";
        yield return "System.IO";
        yield return "System.Linq";
        yield return "System.Net.Http";
        yield return "System.Threading";
        yield return "System.Threading.Tasks";

        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            yield return "System.Net.Http.Json";
            yield return "Microsoft.AspNetCore.Builder";
            yield return "Microsoft.AspNetCore.Hosting";
            yield return "Microsoft.AspNetCore.Http";
            yield return "Microsoft.AspNetCore.Routing";
        }
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
            sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Microsoft.Extensions.Configuration";
            yield return "Microsoft.Extensions.DependencyInjection";
            yield return "Microsoft.Extensions.Hosting";
            yield return "Microsoft.Extensions.Logging";
        }
    }

    // ---- package references from project.assets.json -----------------------------------

    static (List<string> dlls, string? note) PackageRefs(ProjectInfo p, bool autoRestore)
    {
        var assets = Path.Combine(p.Dir, "obj", "project.assets.json");
        if (!File.Exists(assets) && autoRestore)
        {
            // A restore is cheap and needs no compile — do it rather than degrade.
            Proc.Run("dotnet", $"restore \"{p.CsprojPath}\" -v q --nologo", p.Dir);
        }
        if (!File.Exists(assets))
            return ([], $"{p.Name}: no obj/project.assets.json — run `dotnet restore` once for semantic queries. "
                      + "Tier 1 (search/outline/tree/metrics) works without it.");

        try { return (ParseAssets(assets, p.Tfm), null); }
        catch (Exception e) { return ([], $"{p.Name}: could not read project.assets.json ({e.Message})"); }
    }

    /// <summary>
    /// targets -> "&lt;tfm&gt;" -> "Pkg/1.0.0" -> compile -> "lib/net10.0/Foo.dll".
    /// Absolute path = packageFolders root + libraries["Pkg/1.0.0"].path + compile key.
    /// </summary>
    static List<string> ParseAssets(string assetsPath, string tfm)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = doc.RootElement;
        var result = new List<string>();

        var folders = root.TryGetProperty("packageFolders", out var pf)
            ? pf.EnumerateObject().Select(x => x.Name).ToList()
            : [Cache.Root()];

        var libPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("libraries", out var libs))
            foreach (var l in libs.EnumerateObject())
                if (l.Value.TryGetProperty("path", out var pth) && pth.GetString() is { } s)
                    libPaths[l.Name] = s;

        if (!root.TryGetProperty("targets", out var targets)) return result;

        // Prefer the target matching our chosen TFM; else the first (single-TFM projects).
        var target = targets.EnumerateObject()
            .FirstOrDefault(t => t.Name.Equals(tfm, StringComparison.OrdinalIgnoreCase));
        var chosen = target.Value.ValueKind == JsonValueKind.Object
            ? target.Value
            : targets.EnumerateObject().Select(t => t.Value).FirstOrDefault();
        if (chosen.ValueKind != JsonValueKind.Object) return result;

        foreach (var entry in chosen.EnumerateObject())
        {
            // "type": "project" -> a sibling project. It is a source edge (ProjectReference), NOT
            // a metadata reference; adding its dll would duplicate every type it declares.
            if (entry.Value.TryGetProperty("type", out var ty) &&
                ty.GetString() is "project") continue;

            if (!entry.Value.TryGetProperty("compile", out var compile)) continue;
            if (!libPaths.TryGetValue(entry.Name, out var rel)) continue;

            foreach (var c in compile.EnumerateObject())
            {
                var file = c.Name;
                if (Path.GetFileName(file) == "_._") continue;         // placeholder (metapackage)
                foreach (var folder in folders)
                {
                    var abs = Path.GetFullPath(Path.Combine(folder, rel, file.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(abs)) { result.Add(abs); break; }
                }
            }
        }
        return result;
    }

    // ---- shared frameworks (ported from dotnet-reflect: reflect.cs FrameworkDirs) --------

    /// <summary>The newest version dir of every shared framework under dotnet/shared
    /// (Microsoft.NETCore.App, Microsoft.AspNetCore.App, …) so framework base types resolve.</summary>
    public static IEnumerable<string> FrameworkDirs()
    {
        var rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        yield return rt;
        var shared = Directory.GetParent(rt.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Parent;
        if (shared is null) yield break;
        foreach (var fw in shared.GetDirectories())
        {
            var newest = fw.GetDirectories()
                .OrderByDescending(d => d.Name, Comparer<string>.Create(Cache.CompareVer)).FirstOrDefault();
            if (newest is not null) yield return newest.FullName;
        }
    }

    static List<string>? _fwCache;
    public static List<string> FrameworkDlls() => _fwCache ??= FrameworkDirs()
        .Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d, "*.dll"))
        .ToList();
}

/// <summary>Loads document text lazily and with correct encoding detection.</summary>
sealed class FileTextLoaderUtf8(string path) : TextLoader
{
    public override Task<TextAndVersion> LoadTextAndVersionAsync(
        LoadTextOptions options, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = SourceText.From(stream, System.Text.Encoding.UTF8, canBeEmbedded: false);
        return Task.FromResult(TextAndVersion.Create(text, VersionStamp.Create(), path));
    }
}

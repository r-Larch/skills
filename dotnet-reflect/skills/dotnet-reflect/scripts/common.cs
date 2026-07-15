// common.cs — shared helpers (included, never run directly). No external packages.
// Cache location, version sorting, process runner, workbench builder, XML-doc lookup.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class Cache
{
    public static string Root() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public static string PackageDir(string id) => Path.Combine(Root(), id.ToLowerInvariant());

    public static List<string> Versions(string id)
    {
        var d = PackageDir(id);
        return Directory.Exists(d)
            ? SortDesc(Directory.GetDirectories(d).Select(p => Path.GetFileName(p)!)).ToList()
            : new();
    }

    public static IEnumerable<string> SortDesc(IEnumerable<string> vers) =>
        vers.OrderByDescending(v => v, Comparer<string>.Create(CompareVer));

    public static int CompareVer(string a, string b)
    {
        var (na, pa) = Split(a); var (nb, pb) = Split(b);
        for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        bool preA = pa.Length > 0, preB = pb.Length > 0;      // 2.0 > 2.0-beta
        if (preA != preB) return preA ? -1 : 1;
        return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);

        static (int[] nums, string pre) Split(string v)
        {
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v[..dash];
            var pre = dash < 0 ? "" : v[(dash + 1)..];
            return (core.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray(), pre);
        }
    }
}

static class Proc
{
    public static (int code, string output) Run(string file, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(file, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd ?? "" };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, so + se);
    }
}

// A built, ready-to-inspect package: bin folder with full closure + primary assembly + xml.
sealed class Workbench
{
    public bool Ok; public string Error = "";
    public string BinDir = "", Version = "", Log = "", Config = "";
    // The assemblies this package exposes: its own lib for a normal package, or the real assemblies
    // contributed by its dependency subtree for a metapackage (lib/*/_._). Each with its xml doc (or "").
    public List<(string dll, string xml)> Targets = new();
    public string Dll => Targets.Count > 0 ? Targets[0].dll : "";   // convenience for single-target callers
    public string Xml => Targets.Count > 0 ? Targets[0].xml : "";

    public static string RootDir() => Path.Combine(Path.GetTempPath(), "dotnet-reflect-wb");

    // Build (or reuse a cached build of) a temp project referencing <pkgId> <version>.
    // version "latest" => newest cached version if any, else floating "*" (fetches from nuget).
    public static Workbench Ensure(string pkgId, string version)
    {
        var wb = new Workbench();
        bool floating = false;
        var reqVersion = version;
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            var newest = Cache.Versions(pkgId).FirstOrDefault();
            if (newest != null) reqVersion = newest; else { reqVersion = "*"; floating = true; }
        }

        var dir = Path.Combine(RootDir(), Sanitize(pkgId) + "__" + Sanitize(reqVersion));
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "wb.csproj");
        File.WriteAllText(csproj, ProjectXml(pkgId, reqVersion));

        // Respect a nuget.config so private/authenticated feeds (e.g. a GitHub Packages source with
        // credentials from env vars) resolve. The temp workbench isn't under the repo, so NuGet's own
        // directory walk won't find it — we point restore at it explicitly. Credentials referenced as
        // %ENV_VAR% expand at restore time from the inherited (global) environment.
        wb.Config = ResolveConfig();
        var configArg = wb.Config != "" ? $" -p:RestoreConfigFile=\"{wb.Config}\"" : "";

        var binDir = Path.Combine(dir, "bin", "Debug", "net10.0");
        var assets = Path.Combine(dir, "obj", "project.assets.json");
        bool built = File.Exists(assets) && Directory.Exists(binDir) && Directory.GetFiles(binDir, "*.dll").Length > 0;
        if (floating || !built)
        {
            var (code, outp) = Proc.Run("dotnet", $"build \"{csproj}\" -c Debug -v q -nologo{configArg}", dir);
            wb.Log = outp;
            if (code != 0) { wb.Error = $"build failed for {pkgId} {reqVersion}"
                + (wb.Config != "" ? $" (using nuget.config: {wb.Config})" : " (no nuget.config found — private feeds won't resolve; see SKILL.md)")
                + $":\n{Tail(outp)}"; return wb; }
        }
        if (!File.Exists(assets)) { wb.Error = "no project.assets.json produced (restore failed?)."; return wb; }

        var (targets, resolved) = ResolveTargets(assets, pkgId, binDir);
        if (targets.Count == 0)
        {
            wb.Error = $"'{pkgId}' {reqVersion} exposes no net10.0 lib assembly (pure analyzer / runtime / ref-only package, "
                     + "or a metapackage whose dependencies also have none). Inspect it with cache.cs.";
            return wb;
        }
        wb.Ok = true; wb.BinDir = binDir; wb.Version = resolved ?? reqVersion; wb.Targets = targets;
        return wb;
    }

    // Resolve the set of real assemblies a package exposes. Normal package -> its own lib dll.
    // Metapackage (lib/*/_._) -> descend its dependency graph, collecting the first real lib dll of each
    // branch (so OpenIddict.AspNetCore -> OpenIddict.Server.AspNetCore + OpenIddict.Validation.AspNetCore,
    // not their transitive framework deps).
    static (List<(string dll, string xml)> targets, string? version) ResolveTargets(string assetsPath, string rootId, string binDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var outList = new List<(string, string)>();
        if (!doc.RootElement.TryGetProperty("targets", out var targets)) return (outList, null);
        var target = targets.EnumerateObject().Select(p => p.Value).FirstOrDefault();
        if (target.ValueKind != JsonValueKind.Object) return (outList, null);

        // id(lower) -> (entry, id, version)
        var map = new Dictionary<string, (JsonElement entry, string id, string ver)>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in target.EnumerateObject())
        {
            var slash = lib.Name.IndexOf('/');
            var id = slash < 0 ? lib.Name : lib.Name[..slash];
            var ver = slash < 0 ? "" : lib.Name[(slash + 1)..];
            map[id] = (lib.Value, id, ver);
        }

        string? rootVersion = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedDll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string id)
        {
            if (!seen.Add(id) || !map.TryGetValue(id, out var e)) return;
            if (id.Equals(rootId, StringComparison.OrdinalIgnoreCase)) rootVersion = e.ver;
            var libPath = FirstLibDll(e.entry);
            if (libPath != null)                                   // a real assembly — take it, stop descending
            {
                var dllName = Path.GetFileName(libPath);
                if (addedDll.Add(dllName))
                {
                    var binDll = Path.Combine(binDir, dllName);
                    if (File.Exists(binDll)) outList.Add((binDll, ResolveXml(binDir, e.id, e.ver, libPath)));
                }
                return;
            }
            if (e.entry.TryGetProperty("dependencies", out var deps))  // metapackage — descend
                foreach (var d in deps.EnumerateObject()) Visit(d.Name);
        }
        Visit(rootId);
        return (outList, rootVersion);
    }

    static string? FirstLibDll(JsonElement entry)
    {
        foreach (var section in new[] { "compile", "runtime" })
            if (entry.TryGetProperty(section, out var files))
                foreach (var f in files.EnumerateObject())
                {
                    var name = Path.GetFileName(f.Name);
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name != "_._")
                        return f.Name;
                }
        return null;
    }

    // Prefer the xml copied into bin; fall back to the exact path in the NuGet cache.
    static string ResolveXml(string binDir, string id, string ver, string libPath)
    {
        var binXml = Path.Combine(binDir, Path.GetFileNameWithoutExtension(libPath) + ".xml");
        if (File.Exists(binXml)) return binXml;
        var rel = Path.ChangeExtension(libPath.Replace('/', Path.DirectorySeparatorChar), ".xml");
        var cacheXml = Path.Combine(Cache.PackageDir(id), ver, rel);
        return File.Exists(cacheXml) ? cacheXml : "";
    }

    static string ProjectXml(string id, string version) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Library</OutputType>
            <Nullable>disable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
            <CopyDocumentationFilesFromPackages>true</CopyDocumentationFilesFromPackages>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <NoWarn>$(NoWarn);NU1701;NU1603</NoWarn>
          </PropertyGroup>
          <ItemGroup><PackageReference Include="{id}" Version="{version}" /></ItemGroup>
        </Project>
        """;

    // Validate a --bin assembly path; returns "" if OK, else a clean message listing what's available.
    public static string CheckAssembly(string binDir, string dllPath)
    {
        if (File.Exists(dllPath)) return "";
        if (!Directory.Exists(binDir)) return $"binDir not found: {binDir}";
        var have = Directory.GetFiles(binDir, "*.dll").Select(f => Path.GetFileName(f)!).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var want = Path.GetFileName(dllPath);
        var stem = Path.GetFileNameWithoutExtension(want);
        var near = have.Where(h => h.Contains(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        return $"assembly '{want}' not found in {binDir}."
             + (near.Count > 0 ? $"\n  did you mean: {string.Join(", ", near)}" : "")
             + $"\n  available ({have.Count}): {string.Join(", ", have.Take(30))}" + (have.Count > 30 ? " …" : "");
    }

    // Which nuget.config to restore with: explicit override first, else the nearest one walking up from
    // the invocation directory. Returns "" (use NuGet's default hierarchy) if none is found.
    static string ResolveConfig()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_API_CONFIG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
        {
            var hit = dir.GetFiles("nuget.config").FirstOrDefault();   // Windows FS is case-insensitive
            if (hit != null) return hit.FullName;
        }
        return "";
    }

    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
    static string Tail(string s) { var lines = s.Replace("\r", "").Split('\n'); return string.Join("\n", lines.Where(l => l.Trim().Length > 0).TakeLast(12)); }
}

// XML-doc <summary> text, keyed by member id with the parameter list stripped (overloads share).
sealed class XmlDocs
{
    readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    public static XmlDocs Load(string? path)
    {
        var d = new XmlDocs();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return d;
        foreach (var m in System.Xml.Linq.XDocument.Load(path).Descendants("member"))
        {
            var name = (string?)m.Attribute("name"); var s = m.Element("summary");
            if (name is null || s is null) continue;
            var text = string.Join(" ", Flatten(s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var id = name.Length > 2 ? name[2..] : name;
            var paren = id.IndexOf('('); if (paren >= 0) id = id[..paren];
            d._map.TryAdd(id, text);
        }
        return d;
    }

    // Render summary text including inline doc elements that XElement.Value would otherwise drop:
    // <see cref="M:Ns.Type.Member(...)"/> -> Member, <see langword="null"/> -> null,
    // <paramref/>/<typeparamref name/> -> the name, <c>code</c> -> its text.
    static string Flatten(System.Xml.Linq.XElement el)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var node in el.Nodes())
        {
            if (node is System.Xml.Linq.XText t) { sb.Append(t.Value); continue; }
            if (node is not System.Xml.Linq.XElement e) continue;
            switch (e.Name.LocalName)
            {
                case "see":
                case "seealso":
                    var cref = (string?)e.Attribute("cref");
                    var lang = (string?)e.Attribute("langword");
                    if (cref is not null) sb.Append(ShortRef(cref));
                    else if (lang is not null) sb.Append(lang);
                    else if (!string.IsNullOrWhiteSpace(e.Value)) sb.Append(e.Value);
                    else if ((string?)e.Attribute("href") is { } href) sb.Append(href);
                    break;
                case "paramref":
                case "typeparamref":
                    sb.Append((string?)e.Attribute("name") ?? "");
                    break;
                default:
                    sb.Append(Flatten(e));   // <c>, <para>, <b>, … -> inner text
                    break;
            }
        }
        return sb.ToString();
    }

    // "M:Ns.Type.Method(System.Int32)" / "T:Ns.Type" / "P:Ns.Type.Prop" -> the last name segment.
    static string ShortRef(string cref)
    {
        var s = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = s.IndexOf('('); if (paren >= 0) s = s[..paren];
        var dot = s.LastIndexOf('.'); return dot >= 0 ? s[(dot + 1)..] : s;
    }

    public string? Summary(string key) => _map.TryGetValue(key, out var v) ? v : null;
}

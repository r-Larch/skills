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
    public string BinDir = "", Dll = "", Xml = "", Version = "", Log = "";

    public static string RootDir() => Path.Combine(Path.GetTempPath(), "nuget-api-wb");

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

        var binDir = Path.Combine(dir, "bin", "Debug", "net10.0");
        var assets = Path.Combine(dir, "obj", "project.assets.json");
        bool built = File.Exists(assets) && Directory.Exists(binDir) && Directory.GetFiles(binDir, "*.dll").Length > 0;
        if (floating || !built)
        {
            var (code, outp) = Proc.Run("dotnet", $"build \"{csproj}\" -c Debug -v q -nologo", dir);
            wb.Log = outp;
            if (code != 0) { wb.Error = $"build failed for {pkgId} {reqVersion}:\n{Tail(outp)}"; return wb; }
        }
        if (!File.Exists(assets)) { wb.Error = "no project.assets.json produced (restore failed?)."; return wb; }

        var (primary, resolved) = ReadPrimary(assets, pkgId);
        if (primary == null)
        {
            wb.Error = $"'{pkgId}' {reqVersion} exposes no net10.0 lib assembly (meta-package / analyzer / ref-only?). "
                     + "Inspect it with cache.cs.";
            return wb;
        }
        var dll = Path.Combine(binDir, primary);
        if (!File.Exists(dll)) { wb.Error = $"built ok but {primary} missing in {binDir}."; return wb; }

        var xml = Path.Combine(binDir, Path.GetFileNameWithoutExtension(primary) + ".xml");
        wb.Ok = true; wb.BinDir = binDir; wb.Dll = dll; wb.Version = resolved ?? reqVersion;
        wb.Xml = File.Exists(xml) ? xml : FindXmlInCache(pkgId, wb.Version, primary) ?? "";
        return wb;
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

    static (string? primary, string? version) ReadPrimary(string assetsPath, string pkgId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        if (!doc.RootElement.TryGetProperty("targets", out var targets)) return (null, null);
        foreach (var target in targets.EnumerateObject())
            foreach (var lib in target.Value.EnumerateObject())
            {
                var slash = lib.Name.IndexOf('/');
                var id = slash < 0 ? lib.Name : lib.Name[..slash];
                if (!id.Equals(pkgId, StringComparison.OrdinalIgnoreCase)) continue;
                var version = slash < 0 ? null : lib.Name[(slash + 1)..];
                foreach (var section in new[] { "compile", "runtime" })
                    if (lib.Value.TryGetProperty(section, out var files))
                        foreach (var f in files.EnumerateObject())
                        {
                            var name = Path.GetFileName(f.Name);
                            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name != "_._")
                                return (name, version);
                        }
                return (null, version);
            }
        return (null, null);
    }

    static string? FindXmlInCache(string pkgId, string version, string primaryDll)
    {
        var xmlName = Path.GetFileNameWithoutExtension(primaryDll) + ".xml";
        var libRoot = Path.Combine(Cache.PackageDir(pkgId), version, "lib");
        if (!Directory.Exists(libRoot)) return null;
        return Directory.EnumerateFiles(libRoot, xmlName, SearchOption.AllDirectories).FirstOrDefault();
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
            var text = string.Join(" ", s.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var id = name.Length > 2 ? name[2..] : name;
            var paren = id.IndexOf('('); if (paren >= 0) id = id[..paren];
            d._map.TryAdd(id, text);
        }
        return d;
    }
    public string? Summary(string key) => _map.TryGetValue(key, out var v) ? v : null;
}

#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2072;IL2075;CA2266
#:include common.cs
#:include reflect.cs
// diff.cs — API-surface changelog between two versions of a package. Builds a workbench for each,
// renders both surfaces, and prints added/removed types and, per common type, added/removed members.
//
//   dotnet run diff.cs <pkgId> <v1> <v2> [typeFilter]     (versions may be "latest")
using System;
using System.Text;

if (args.Length < 3) { Console.Error.WriteLine("usage: diff.cs <pkgId> <v1> <v2> [typeFilter]"); return 1; }
var (id, v1, v2) = (args[0], args[1], args[2]);
var filter = args.Length > 3 ? args[3] : "";

string Surface(string version)
{
    var wb = Workbench.Ensure(id, version);
    if (!wb.Ok) throw new Exception($"{id} {version}: {wb.Error}");
    var sb = new StringBuilder();
    foreach (var (dll, xml) in wb.Targets)   // combine all exposed assemblies into one surface
    {
        using var loaded = Loaded.Open(wb.BinDir, dll);
        sb.Append(Render.Surface(loaded.Types, filter, XmlDocs.Load(xml)));
    }
    return sb.ToString();
}

string s1, s2;
try { s1 = Surface(v1); s2 = Surface(v2); }
catch (Exception ex) { Console.Error.WriteLine("dotnet-reflect: " + ex.Message); return 2; }

var (order1, map1) = Parse(s1);
var (order2, map2) = Parse(s2);

var sb = new StringBuilder();
sb.AppendLine($"--- {id} {v1}");
sb.AppendLine($"+++ {id} {v2}");
int changes = 0;

foreach (var t in order1.Where(t => !map2.ContainsKey(t))) { sb.AppendLine($"\n- {map1[t].header}"); changes++; }
foreach (var t in order2.Where(t => !map1.ContainsKey(t))) { sb.AppendLine($"\n+ {map2[t].header}"); changes++; }

foreach (var t in order2.Where(map1.ContainsKey))
{
    var (h1, m1) = map1[t]; var (h2, m2) = map2[t];
    var removed = m1.Where(m => !m2.Contains(m)).ToList();
    var added = m2.Where(m => !m1.Contains(m)).ToList();
    if (h1 == h2 && removed.Count == 0 && added.Count == 0) continue;
    changes++;
    sb.AppendLine($"\n~ {h2}");
    if (h1 != h2) sb.AppendLine($"    (declaration was: {h1})");
    foreach (var m in removed) sb.AppendLine($"    - {m.Trim()}");
    foreach (var m in added) sb.AppendLine($"    + {m.Trim()}");
}

if (changes == 0) sb.AppendLine("\n// no public API differences" + (filter == "" ? "." : $" for types matching \"{filter}\"."));
Console.Write(sb.ToString());
return 0;

// --- parse Render.Surface output into type -> (header, member lines), comments stripped ---
static (List<string> order, Dictionary<string, (string header, HashSet<string> members)> map) Parse(string text)
{
    var order = new List<string>();
    var map = new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal);
    string? cur = null;
    foreach (var raw in text.Replace("\r", "").Split('\n'))
    {
        if (raw.Length == 0) continue;
        bool isHeader = raw.Length > 0 && raw[0] != ' ' &&
            (raw.StartsWith("class ") || raw.StartsWith("interface ") || raw.StartsWith("struct ") || raw.StartsWith("enum "));
        if (isHeader)
        {
            var header = Strip(raw);
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            cur = parts.Length > 1 ? parts[1] : header;
            if (!map.ContainsKey(cur)) { order.Add(cur); map[cur] = (header, new HashSet<string>(StringComparer.Ordinal)); }
        }
        else if (raw.StartsWith("  ") && cur != null)
        {
            var m = Strip(raw).Trim();
            if (m.Length > 0 && !m.StartsWith("//")) map[cur].Item2.Add(m);
        }
    }
    return (order, map);

    static string Strip(string line) { var i = line.IndexOf("   // ", StringComparison.Ordinal); return i >= 0 ? line[..i] : line; }
}

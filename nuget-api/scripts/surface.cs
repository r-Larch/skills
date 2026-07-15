#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2072;IL2075
// surface.cs — dump the PUBLIC API surface of a .NET assembly (load-only reflection),
// merged with XML-doc <summary> text when available.
//
//   dotnet run surface.cs <binDir> <Assembly.dll> [typeFilter] [--xml <path>]
//
// <binDir>    a folder holding the full dependency closure (a project's bin/<cfg>/<tfm>/ output).
// typeFilter  case-insensitive substring on the type FullName (omit to dump everything public).
// --xml       path to the .xml doc file; defaults to <binDir>/<AssemblyName>.xml if present.
using System.Reflection;
using System.Text;
using System.Xml.Linq;

if (args.Length < 2) { Console.Error.WriteLine("usage: surface.cs <binDir> <Assembly.dll> [typeFilter] [--xml <path>]"); return 1; }
var binDir = args[0];
var target = Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(binDir, args[1]);
string filter = "", xmlPath = "";
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--xml" && i + 1 < args.Length) xmlPath = args[++i];
    else filter = args[i];
}
if (xmlPath == "")
{
    var guess = Path.Combine(binDir, Path.GetFileNameWithoutExtension(target) + ".xml");
    if (File.Exists(guess)) xmlPath = guess;
}

// --- XML doc: map normalized member-id -> one-line summary ---
var docs = new Dictionary<string, string>(StringComparer.Ordinal);
if (xmlPath != "" && File.Exists(xmlPath))
    foreach (var m in XDocument.Load(xmlPath).Descendants("member"))
    {
        var name = (string?)m.Attribute("name"); if (name is null) continue;
        var summary = m.Element("summary"); if (summary is null) continue;
        var text = string.Join(" ", summary.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var id = name.Length > 2 ? name[2..] : name;          // drop "M:"/"T:"/"P:"/"F:"/"E:"
        var paren = id.IndexOf('('); if (paren >= 0) id = id[..paren];   // drop param list -> overloads share
        docs.TryAdd(id, text);
    }
string? Doc(string key) => docs.TryGetValue(key, out var v) ? v : null;

// --- load-only reflection over the full closure ---
var rtDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var paths = Directory.GetFiles(binDir, "*.dll").Concat(Directory.GetFiles(rtDir, "*.dll")).Distinct().ToArray();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
var asm = mlc.LoadFromAssemblyPath(target);

string N(Type t) => t.IsGenericType
    ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(N))}>"
    : (t.IsArray ? N(t.GetElementType()!) + "[]" : t.Name);

Type[] types;
try { types = asm.GetExportedTypes(); }
catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }

var sb = new StringBuilder();
foreach (var type in types
         .Where(t => filter == "" || (t.FullName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
         .OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
    var baseSuffix = type.BaseType is { } b && b.Name != "Object" && b.Name != "ValueType" ? $" : {N(b)}" : "";
    sb.Append($"\n{kind} {type.FullName}{baseSuffix}");
    if (Doc(type.FullName ?? "") is { } td) sb.Append($"   // {td}");
    sb.AppendLine();
    try
    {
        foreach (var c in type.GetConstructors().Where(c => c.IsPublic))
            sb.AppendLine($"  .ctor({Ps(c)})");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            sb.AppendLine($"  {N(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.GetSetMethod() is not null ? "set; " : "")}}}{Tail(type, p.Name)}");
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
            sb.AppendLine($"  {N(m.ReturnType)} {m.Name}({Ps(m)}){Tail(type, m.Name)}");
        if (type.IsEnum)
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                sb.AppendLine($"  = {f.Name}");
    }
    catch (Exception ex) { sb.AppendLine($"  <!-- members unresolved: {ex.GetType().Name} -->"); }

    string Ps(MethodBase mb) => string.Join(", ", mb.GetParameters().Select(p => $"{N(p.ParameterType)} {p.Name}"));
    string Tail(Type t, string member) => Doc($"{t.FullName}.{member}") is { } d ? $"   // {d}" : "";
}
Console.Write(sb.ToString());
return 0;

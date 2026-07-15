#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2072;IL2075
// find.cs — locate types and members by name across an assembly (case-insensitive substring).
// Answers "what is it called / where does it live" without dumping the whole surface.
//
//   dotnet run find.cs <binDir> <Assembly.dll> <pattern>
//
// Prints one match per line:  <kind>  <Type.Member>  <signature>
using System.Reflection;

if (args.Length < 3) { Console.Error.WriteLine("usage: find.cs <binDir> <Assembly.dll> <pattern>"); return 1; }
var binDir = args[0];
var target = Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(binDir, args[1]);
var pat = args[2];
bool M(string s) => s.Contains(pat, StringComparison.OrdinalIgnoreCase);

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

const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    if (M(type.Name)) Console.WriteLine($"{(type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class"),-9} {type.FullName}");
    try
    {
        foreach (var p in type.GetProperties(F).Where(p => M(p.Name)))
            Console.WriteLine($"{"prop",-9} {type.FullName}.{p.Name}   {N(p.PropertyType)}");
        foreach (var m in type.GetMethods(F).Where(m => !m.IsSpecialName && M(m.Name)))
            Console.WriteLine($"{"method",-9} {type.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(x => N(x.ParameterType)))})   -> {N(m.ReturnType)}");
        if (type.IsEnum)
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => M(f.Name)))
                Console.WriteLine($"{"enum-val",-9} {type.FullName}.{f.Name}");
    }
    catch { /* unresolved member type — skip */ }
}
return 0;

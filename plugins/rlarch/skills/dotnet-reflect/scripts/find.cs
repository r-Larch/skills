#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2072;IL2075;CA2266
#:include common.cs
#:include reflect.cs
// find.cs — locate types & members by name (case-insensitive substring) across an assembly.
//
//   dotnet run find.cs <pkgId> <version> <pattern>          (version may be "latest")
//   dotnet run find.cs --bin <binDir> <Assembly.dll> <pattern>
using System;
using System.Reflection;

string binDir, pat;
List<(string dll, string xml)> targets;
if (args.Length >= 1 && args[0] == "--bin")
{
    if (args.Length < 4) { Console.Error.WriteLine("usage: find.cs --bin <dir> <Assembly.dll> <pattern>"); return 1; }
    binDir = args[1];
    var dll = Path.IsPathRooted(args[2]) ? args[2] : Path.Combine(binDir, args[2]);
    if (Workbench.CheckAssembly(binDir, dll) is { Length: > 0 } err) { Console.Error.WriteLine("dotnet-reflect: " + err); return 2; }
    targets = new() { (dll, "") };
    pat = args[3];
}
else
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: find.cs <pkgId> <version> <pattern>   |   --bin <dir> <Assembly.dll> <pattern>"); return 1; }
    var wb = Workbench.Ensure(args[0], args[1]);
    if (!wb.Ok) { Console.Error.WriteLine("dotnet-reflect: " + wb.Error); return 2; }
    binDir = wb.BinDir; targets = wb.Targets; pat = args[2];
    Console.WriteLine($"// {args[0]} {wb.Version} — matches for \"{pat}\" across {targets.Count} assembly(ies):");
}
bool M(string s) => s.Contains(pat, StringComparison.OrdinalIgnoreCase);

const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
int hits = 0, skipped = 0;
foreach (var (dll, _) in targets)
{
    using var loaded = Loaded.Open(binDir, dll);
    if (loaded.Diagnostics != "") Console.Write(loaded.Diagnostics);
    var asm = targets.Count > 1 ? Path.GetFileNameWithoutExtension(dll) + "!" : "";
    foreach (var type in loaded.Types.OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        try
        {
            // IsValueType resolves the base type and can throw on a missing dependency — keep it inside the try.
            if (M(type.Name)) { Console.WriteLine($"{(type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class"),-9} {asm}{type.FullName}"); hits++; }
            foreach (var p in type.GetProperties(F).Where(p => M(p.Name)))
            { Console.WriteLine($"{"prop",-9} {asm}{type.FullName}.{p.Name}   {Sig.N(p.PropertyType)}"); hits++; }
            foreach (var m in type.GetMethods(F).Where(m => !m.IsSpecialName && M(m.Name)))
            { Console.WriteLine($"{"method",-9} {asm}{type.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(x => Sig.N(x.ParameterType)))})   -> {Sig.N(m.ReturnType)}"); hits++; }
            if (type.IsEnum)
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => M(f.Name)))
                { Console.WriteLine($"{"enum-val",-9} {asm}{type.FullName}.{f.Name}"); hits++; }
        }
        catch { skipped++; }   // a member type failed to resolve — count and report below
    }
}
if (hits == 0) Console.WriteLine($"// no matches for \"{pat}\".");
if (skipped > 0) Console.WriteLine($"// NOTE: {skipped} type(s) skipped — a member type could not be resolved (missing dependency). Matches may be incomplete.");
return 0;

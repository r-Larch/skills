#:package ICSharpCode.Decompiler@10.1.1.8388
#:property NoWarn=CA2266
#:include common.cs
// decompile.cs — decompile a type (or whole assembly) to real C# WITH method bodies.
// Reveals behavior signatures can't: defaults, control flow, how an overload delegates.
//
//   dotnet run decompile.cs <pkgId> <version> [Namespace.TypeName]   (version may be "latest")
//   dotnet run decompile.cs --bin <binDir> <Assembly.dll> [Namespace.TypeName]
//
// Omit the type name to list every public full type name (across all exposed assemblies).
using System;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

List<string> dlls; string? typeName;
if (args.Length >= 1 && args[0] == "--bin")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: decompile.cs --bin <dir> <Assembly.dll> [Type]"); return 1; }
    var dll = Path.IsPathRooted(args[2]) ? args[2] : Path.Combine(args[1], args[2]);
    if (Workbench.CheckAssembly(args[1], dll) is { Length: > 0 } err) { Console.Error.WriteLine("dotnet-reflect: " + err); return 2; }
    dlls = new() { dll };
    typeName = args.Length > 3 ? args[3] : null;
}
else
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: decompile.cs <pkgId> <version> [Type]   |   --bin <dir> <Assembly.dll> [Type]"); return 1; }
    var wb = Workbench.Ensure(args[0], args[1]);
    if (!wb.Ok) { Console.Error.WriteLine("dotnet-reflect: " + wb.Error); return 2; }
    dlls = wb.Targets.Select(t => t.dll).ToList();
    typeName = args.Length > 2 ? args[2] : null;
    Console.WriteLine($"// {args[0]} {wb.Version} — {dlls.Count} assembly(ies)");
}

var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };

if (typeName is null)
{
    foreach (var dll in dlls)
    {
        if (dlls.Count > 1) Console.WriteLine($"\n// ===================== {Path.GetFileName(dll)} =====================");
        var dc = new CSharpDecompiler(dll, settings);
        foreach (var t in dc.TypeSystem.MainModule.TypeDefinitions
                 .Where(t => t.Accessibility == Accessibility.Public)
                 .Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine(t);
    }
    return 0;
}

foreach (var dll in dlls)   // find the assembly that actually declares the type, then decompile it there
{
    var dc = new CSharpDecompiler(dll, settings);
    if (dc.TypeSystem.MainModule.TypeDefinitions.Any(t => t.FullName == typeName))
    {
        if (dlls.Count > 1) Console.WriteLine($"// in {Path.GetFileName(dll)}:");
        Console.WriteLine(dc.DecompileTypeAsString(new FullTypeName(typeName)));
        return 0;
    }
}
Console.Error.WriteLine($"dotnet-reflect: type '{typeName}' not found in {string.Join(", ", dlls.Select(Path.GetFileName))}. "
                      + "List types by omitting the type name, or check the namespace.");
return 3;

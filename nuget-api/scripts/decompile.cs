#:package ICSharpCode.Decompiler@10.1.1.8388
#:property NoWarn=CA2266
#:include common.cs
// decompile.cs — decompile a type (or whole assembly) to real C# WITH method bodies.
// Reveals behavior signatures can't: defaults, control flow, how an overload delegates.
//
//   dotnet run decompile.cs <pkgId> <version> [Namespace.TypeName]   (version may be "latest")
//   dotnet run decompile.cs --bin <binDir> <Assembly.dll> [Namespace.TypeName]
//
// Omit the type name to list every public full type name in the assembly.
using System;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

string dll; string? typeName;
if (args.Length >= 1 && args[0] == "--bin")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: decompile.cs --bin <dir> <Assembly.dll> [Type]"); return 1; }
    dll = Path.IsPathRooted(args[2]) ? args[2] : Path.Combine(args[1], args[2]);
    typeName = args.Length > 3 ? args[3] : null;
}
else
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: decompile.cs <pkgId> <version> [Type]   |   --bin <dir> <Assembly.dll> [Type]"); return 1; }
    var wb = Workbench.Ensure(args[0], args[1]);
    if (!wb.Ok) { Console.Error.WriteLine("nuget-api: " + wb.Error); return 2; }
    dll = wb.Dll; typeName = args.Length > 2 ? args[2] : null;
    Console.WriteLine($"// {args[0]} {wb.Version}   (assembly: {Path.GetFileName(dll)})");
}

var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
var decompiler = new CSharpDecompiler(dll, settings);

if (typeName is null)
{
    foreach (var t in decompiler.TypeSystem.MainModule.TypeDefinitions
             .Where(t => t.Accessibility == Accessibility.Public)
             .Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal))
        Console.WriteLine(t);
    return 0;
}
Console.WriteLine(decompiler.DecompileTypeAsString(new FullTypeName(typeName)));
return 0;

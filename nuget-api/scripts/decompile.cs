#:package ICSharpCode.Decompiler@10.1.1.8388
// decompile.cs — decompile a type (or whole assembly) to real C# WITH method bodies.
// Behavior you can't get from signatures: defaults, control flow, how an overload delegates.
//
//   dotnet run decompile.cs <binDir> <Assembly.dll> [Namespace.TypeName]
//
// <binDir>  folder with the full dependency closure (a project's bin/<cfg>/<tfm>/ output);
//           the decompiler resolves references from here automatically.
// TypeName  fully-qualified type to decompile. Omit to list every full type name in the assembly.
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

if (args.Length < 2) { Console.Error.WriteLine("usage: decompile.cs <binDir> <Assembly.dll> [Namespace.TypeName]"); return 1; }
var binDir = args[0];
var target = Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(binDir, args[1]);

var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
var decompiler = new CSharpDecompiler(target, settings);

if (args.Length < 3)
{
    foreach (var t in decompiler.TypeSystem.MainModule.TypeDefinitions
             .Where(t => t.Accessibility == Accessibility.Public)
             .Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal))
        Console.WriteLine(t);
    return 0;
}

var name = new FullTypeName(args[2]);
Console.WriteLine(decompiler.DecompileTypeAsString(name));
return 0;

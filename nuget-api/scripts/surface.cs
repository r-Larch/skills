#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2072;IL2075;CA2266
#:include common.cs
#:include reflect.cs
// surface.cs — public API surface (exact signatures) merged with XML-doc summaries.
//
//   dotnet run surface.cs <pkgId> <version> [typeFilter] [--inherited]   (version may be "latest")
//   dotnet run surface.cs --bin <binDir> <Assembly.dll> [typeFilter] [--inherited]
//
// Builds a workbench for the package automatically (no binDir hunting) and finds the XML itself.
// Metapackages (e.g. OpenIddict.AspNetCore) expand to the real assemblies they expose.
// Members are declared-only (own) by default; --inherited also lists base members grouped by type.
using System;

var showInherited = args.Contains("--inherited") || args.Contains("-i");
args = args.Where(a => a is not ("--inherited" or "-i")).ToArray();
if (args.Length < 2) { Console.Error.WriteLine("usage: surface.cs <pkgId> <version> [typeFilter] [--inherited]   |   --bin <dir> <Assembly.dll> [typeFilter] [--inherited]"); return 1; }

string binDir, filter;
List<(string dll, string xml)> targets;
if (args[0] == "--bin")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: surface.cs --bin <dir> <Assembly.dll> [typeFilter]"); return 1; }
    binDir = args[1];
    var dll = Path.IsPathRooted(args[2]) ? args[2] : Path.Combine(binDir, args[2]);
    var guess = Path.Combine(binDir, Path.GetFileNameWithoutExtension(dll) + ".xml");
    targets = new() { (dll, File.Exists(guess) ? guess : "") };
    filter = args.Length > 3 ? args[3] : "";
}
else
{
    var wb = Workbench.Ensure(args[0], args[1]);
    if (!wb.Ok) { Console.Error.WriteLine("nuget-api: " + wb.Error); return 2; }
    binDir = wb.BinDir; targets = wb.Targets; filter = args.Length > 2 ? args[2] : "";
    Console.WriteLine($"// {args[0]} {wb.Version} — {targets.Count} assembly(ies): {string.Join(", ", targets.Select(t => Path.GetFileName(t.dll)))}");
}

int shown = 0;
foreach (var (dll, xml) in targets)
{
    using var loaded = Loaded.Open(binDir, dll);
    var body = Render.Surface(loaded.Types, filter, XmlDocs.Load(xml), showInherited);
    if (string.IsNullOrWhiteSpace(body) && loaded.Diagnostics == "") continue;   // skip assemblies with no matching types
    if (targets.Count > 1) Console.WriteLine($"\n// ===================== {Path.GetFileName(dll)} =====================");
    if (loaded.Diagnostics != "") Console.Write(loaded.Diagnostics);
    Console.Write(body);
    shown++;
}
if (shown == 0 && filter != "") Console.WriteLine($"// no public types matching \"{filter}\" in {targets.Count} assembly(ies).");
return 0;

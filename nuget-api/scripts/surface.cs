#:package System.Reflection.MetadataLoadContext@9.0.0
#:property NoWarn=IL2026;IL2070;IL2072;IL2075;CA2266
#:include common.cs
#:include reflect.cs
// surface.cs — public API surface (exact signatures) merged with XML-doc summaries.
//
//   dotnet run surface.cs <pkgId> <version> [typeFilter]     (version may be "latest")
//   dotnet run surface.cs --bin <binDir> <Assembly.dll> [typeFilter]
//
// Builds a workbench for the package automatically (no binDir hunting) and finds the XML itself.
using System;

if (args.Length < 2) { Console.Error.WriteLine("usage: surface.cs <pkgId> <version> [typeFilter]   |   --bin <dir> <Assembly.dll> [typeFilter]"); return 1; }

string binDir, dll, xml = "", filter;
if (args[0] == "--bin")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: surface.cs --bin <dir> <Assembly.dll> [typeFilter]"); return 1; }
    binDir = args[1];
    dll = Path.IsPathRooted(args[2]) ? args[2] : Path.Combine(binDir, args[2]);
    filter = args.Length > 3 ? args[3] : "";
    var guess = Path.Combine(binDir, Path.GetFileNameWithoutExtension(dll) + ".xml");
    if (File.Exists(guess)) xml = guess;
}
else
{
    var wb = Workbench.Ensure(args[0], args[1]);
    if (!wb.Ok) { Console.Error.WriteLine("nuget-api: " + wb.Error); return 2; }
    binDir = wb.BinDir; dll = wb.Dll; xml = wb.Xml; filter = args.Length > 2 ? args[2] : "";
    Console.WriteLine($"// {args[0]} {wb.Version}   (assembly: {Path.GetFileName(dll)}{(xml == "" ? ", no xml docs" : "")})");
}

using var loaded = Loaded.Open(binDir, dll);
if (loaded.Diagnostics != "") Console.Write(loaded.Diagnostics);
Console.Write(Render.Surface(loaded.Types, filter, XmlDocs.Load(xml)));
return 0;

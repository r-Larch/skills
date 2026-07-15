#:property NoWarn=CA2266
#:include common.cs
// bindir.cs — create & `dotnet build` a temp project that references the package, producing a
// bin folder with the FULL dependency closure (+ xml). Prints the paths for reuse.
//
//   dotnet run bindir.cs <pkgId> <version>      (version may be "latest")
//
// Use the printed binDir with any other script's --bin form, e.g.
//   dotnet run surface.cs --bin "<binDir>" <Assembly.dll> <TypeFilter>
// The workbench is cached under the temp dir and reused, so this is cheap to call again.
using System;

if (args.Length < 2) { Console.Error.WriteLine("usage: bindir.cs <pkgId> <version>"); return 1; }
var wb = Workbench.Ensure(args[0], args[1]);
if (!wb.Ok) { Console.Error.WriteLine("dotnet-reflect: " + wb.Error); return 2; }

Console.WriteLine($"package  : {args[0]} {wb.Version}");
Console.WriteLine($"config   : {(wb.Config == "" ? "(NuGet default hierarchy — no nuget.config found)" : wb.Config)}");
Console.WriteLine($"binDir   : {wb.BinDir}");
Console.WriteLine($"assemblies ({wb.Targets.Count}):");
foreach (var (dll, xml) in wb.Targets)
    Console.WriteLine($"  {Path.GetFileName(dll),-45} {(xml == "" ? "(no xml)" : "xml: " + Path.GetFileName(xml))}");
Console.WriteLine($"\n// reuse one: dotnet run surface.cs --bin \"{wb.BinDir}\" {Path.GetFileName(wb.Dll)} <TypeFilter>");
return 0;

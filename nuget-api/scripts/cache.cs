#:property NoWarn=CA2266
#:include common.cs
// cache.cs — locate a package in the local NuGet cache and list what's there. No build.
//
//   dotnet run cache.cs <pkgId> [version]      (version defaults to newest cached)
//
// Prints the cache path, all cached versions, and the lib TFMs + dll/xml files for one version.
using System;

if (args.Length < 1) { Console.Error.WriteLine("usage: cache.cs <pkgId> [version]"); return 1; }
var id = args[0];
Console.WriteLine($"cache root : {Cache.Root()}");
var pkgDir = Cache.PackageDir(id);
Console.WriteLine($"package dir: {pkgDir}   {(Directory.Exists(pkgDir) ? "" : "(NOT cached — run bindir.cs to fetch, or specify a version)")}");

var versions = Cache.Versions(id);
if (versions.Count == 0) { Console.WriteLine("no cached versions."); return 0; }
Console.WriteLine($"versions   : {string.Join(", ", versions)}");

var version = args.Length > 1 ? args[1] : versions[0];
var vdir = Path.Combine(pkgDir, version);
if (!Directory.Exists(vdir)) { Console.Error.WriteLine($"version {version} not cached. cached: {string.Join(", ", versions)}"); return 2; }

Console.WriteLine($"\n{id} {version}:");
var lib = Path.Combine(vdir, "lib");
if (!Directory.Exists(lib)) { Console.WriteLine("  (no lib/ — analyzer, meta-package, or ref-only package)"); return 0; }
foreach (var tfm in Directory.GetDirectories(lib).OrderBy(x => x, StringComparer.Ordinal))
{
    Console.WriteLine($"  lib/{Path.GetFileName(tfm)}/");
    foreach (var f in Directory.GetFiles(tfm).OrderBy(x => x, StringComparer.Ordinal))
        Console.WriteLine($"    {Path.GetFileName(f)}");
}
Console.WriteLine("\n// Next: dotnet run surface.cs " + id + " " + version + " <TypeFilter>   (builds a workbench automatically)");
return 0;

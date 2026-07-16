// Common.cs — small shared helpers. Cache/CompareVer/Proc are ported from dotnet-reflect's
// scripts/common.cs so the two skills resolve the NuGet cache and sort versions identically.
using System.Diagnostics;

namespace DotnetSource;

static class Cache
{
    public static string Root() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>Semver-ish compare: numeric segments, then `2.0 &gt; 2.0-beta`.</summary>
    public static int CompareVer(string a, string b)
    {
        var (na, pa) = Split(a); var (nb, pb) = Split(b);
        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        bool preA = pa.Length > 0, preB = pb.Length > 0;
        if (preA != preB) return preA ? -1 : 1;
        return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);

        static (int[] nums, string pre) Split(string v)
        {
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v[..dash];
            var pre = dash < 0 ? "" : v[(dash + 1)..];
            return (core.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray(), pre);
        }
    }
}

static class Proc
{
    public static (int code, string output) Run(string file, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? "",
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, so + se);
    }
}

// Index.cs — the on-disk parse index. This is keep-alive layer 2 for Tier 1.
//
// Each stateless run would otherwise re-parse every .cs in the solution. Instead we persist the
// EXTRACTED DECLARATION RECORDS (never SyntaxTrees — they aren't serializable and Tier 1 doesn't
// need them) keyed by (path, mtime, size). Only changed files are re-parsed.
//
// Scope boundary, stated plainly: this accelerates TIER 1 ONLY. Tier 2 needs live SyntaxTrees and
// a Compilation, which no on-disk index can hold — that's what `serve` is for.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetSource;

sealed class FileEntry
{
    public long Mtime { get; set; }
    public long Size { get; set; }
    public List<Decl> Decls { get; set; } = [];
}

sealed class IndexFile
{
    public int Version { get; set; } = IndexVersion;
    public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Bump when Decl's shape or the extraction logic changes, so a stale index is discarded
    // rather than silently served.
    public const int IndexVersion = 1;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(IndexFile))]
partial class IndexJson : JsonSerializerContext;

static class DeclIndex
{
    /// <summary>
    /// Declarations for the whole solution, reusing the on-disk index for unchanged files.
    /// Never throws on a bad index — a corrupt/absent index just means a full parse.
    /// </summary>
    public static List<Decl> Load(SolutionSet set, bool useIndex = true)
    {
        if (!useIndex) return Parser.ParseAll(set);

        var path = IndexPath(set);
        var index = Read(path);

        // Current file set with stat, so we can tell hits from misses and spot ghosts.
        var current = new Dictionary<string, (string proj, long mtime, long size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in set.Projects)
            foreach (var f in p.Files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists) current[f] = (p.Name, fi.LastWriteTimeUtc.Ticks, fi.Length);
                }
                catch { /* unreadable: treat as absent */ }
            }

        var stale = new List<(string file, string proj)>();
        foreach (var (file, info) in current)
            if (!index.Files.TryGetValue(file, out var e) || e.Mtime != info.mtime || e.Size != info.size)
                stale.Add((file, info.proj));

        // Reconcile: drop entries whose file is gone (renamed or deleted). Without this the index
        // grows forever and ghost declarations inflate metrics/search.
        var ghosts = index.Files.Keys.Where(k => !current.ContainsKey(k)).ToList();
        foreach (var g in ghosts) index.Files.Remove(g);

        if (stale.Count > 0)
        {
            var parsed = new System.Collections.Concurrent.ConcurrentBag<(string file, FileEntry entry)>();
            Parallel.ForEach(stale, w =>
            {
                var decls = Parser.ParseFile(w.file, w.proj);
                var info = current[w.file];
                parsed.Add((w.file, new FileEntry { Mtime = info.mtime, Size = info.size, Decls = decls }));
            });
            foreach (var (file, entry) in parsed) index.Files[file] = entry;
        }

        if (stale.Count > 0 || ghosts.Count > 0) Write(path, index);

        return current.Keys
            .Select(f => index.Files.TryGetValue(f, out var e) ? e.Decls : null)
            .Where(d => d is not null)
            .SelectMany(d => d!)
            .ToList();
    }

    /// <summary>How many files the last Load had to re-parse — surfaced by --stats.</summary>
    public static int LastStale { get; private set; }

    static IndexFile Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new IndexFile();
            var f = JsonSerializer.Deserialize(File.ReadAllText(path), IndexJson.Default.IndexFile);
            if (f is null || f.Version != IndexFile.IndexVersion) return new IndexFile();
            return f;
        }
        catch { return new IndexFile(); }   // corrupt index is not an error: just re-parse
    }

    static void Write(string path, IndexFile index)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // temp + rename so a concurrent reader never sees a half-written index.
            var tmp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(index, IndexJson.Default.IndexFile));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* a lost index update just costs a re-parse next run */ }
    }

    /// <summary>
    /// The index key. Three things must be in it, each for a reason we hit in practice:
    ///  * root/solution — obviously.
    ///  * exclusion profile — an --include-generated run sees a different file set and must not
    ///    poison the default index (or vice versa).
    ///  * TOOL BUILD IDENTITY (the assembly MVID, which changes on every recompile) — otherwise a
    ///    rebuilt tool serves an index produced by the PREVIOUS extraction logic. IndexVersion below
    ///    only catches changes to Decl's *shape*, and only if a human remembers to bump it; the MVID
    ///    catches every logic change for free. Cheap insurance against a whole class of
    ///    silently-stale results.
    /// </summary>
    static string IndexPath(SolutionSet set)
    {
        var mvid = typeof(DeclIndex).Assembly.ManifestModule.ModuleVersionId;
        var key = $"{set.SolutionFile ?? set.Root}|{set.Projects.Count}|gen={set.IncludeGenerated}|tool={mvid}";
        return Path.Combine(Paths.IndexDir, $"{Hash(key)}.json");
    }

    public static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

static class Paths
{
    public static string CacheRoot =>
        Environment.GetEnvironmentVariable("DOTNET_SOURCE_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "dotnet-source");

    public static string IndexDir => Path.Combine(CacheRoot, "index");
    public static string ToolDir => Path.Combine(CacheRoot, "tool");
}

// Args.cs — tiny hand-rolled arg parser (mirrors dotnet-reflect's no-dependency style).
// Positionals + `--flag` / `--flag value` / `--flag=value`. No System.CommandLine.
namespace DotnetSource;

sealed class Args
{
    readonly List<string> _pos = [];
    readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Positional => _pos;

    public Args(IEnumerable<string> argv)
    {
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { _pos.Add(a); continue; }

            var name = a[2..];
            var eq = name.IndexOf('=');
            if (eq >= 0) { _flags[name[..eq]] = name[(eq + 1)..]; continue; }

            // `--flag value` only when the next token isn't itself a flag; else it's a boolean.
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            { _flags[name] = list[++i]; continue; }

            _flags[name] = null;
        }
    }

    public bool Has(string name) => _flags.ContainsKey(name);
    public string? Str(string name) => _flags.TryGetValue(name, out var v) ? v : null;
    public string Str(string name, string fallback) => Str(name) ?? fallback;
    public string? At(int i) => i < _pos.Count ? _pos[i] : null;

    public int Int(string name, int fallback) =>
        int.TryParse(Str(name), out var n) ? n : fallback;

    /// <summary>Comma-separated flag value -> set. `--kind class,method` -> {class, method}.</summary>
    public HashSet<string> Csv(string name) =>
        new((Str(name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Flags the command didn't consume — surfaced so typos don't fail silently.</summary>
    public IEnumerable<string> Unknown(params string[] known)
    {
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        return _flags.Keys.Where(k => !set.Contains(k));
    }
}

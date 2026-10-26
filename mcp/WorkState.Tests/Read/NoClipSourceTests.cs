using System.Text.RegularExpressions;
using Xunit;

namespace WorkState.Tests.Read;

/// <summary>
/// noclip. Five times the server printed a shortened copy of a text where the reader needed the
/// whole of it — intent, evidence, notes, a verify_cmd cut inside a printed work_verify route, a
/// criterion in a refusal — and each time the fix was local. This scan makes the rule structural:
/// text is shortened only by <c>Glyph.Row</c>, and only at the places listed below.
/// <para>
/// The two allow-lists are the exception list of <c>docs/workstate/06-rendering.md</c>, "What may be
/// shortened": a list row that carries the ref whose detail view prints the text whole, or a write
/// confirmation echoing the caller's own input. Adding an entry is a review decision; it must name
/// which of the two it is.
/// </para>
/// </summary>
public sealed partial class NoClipSourceTests
{
    /// <summary><c>file:method</c> → why it may call <c>Glyph.Row</c>.</summary>
    private static readonly Dictionary<string, string> RowCallers = new(StringComparer.Ordinal)
    {
        ["Rendering/BoardRenderer.cs:Row"] = "row: a board row carries the ref; work_get prints the title",
        ["Rendering/BoardRenderer.cs:TreeRow"] = "row: a work_tree row carries the ref; work_get prints the title",
        ["Board/BoardReader.Views.cs:SearchAsync"] = "row: a search hit carries its ref; work_get prints it",
        ["Board/BoardReader.Views.cs:ThreadsAsync"] = "row: a finding/followup row carries its ref; work_get prints the title",
        ["Board/BoardReader.cs:AttentionAsync"] = "row: the blocker attention line carries the finding ref",
        ["Writing/WorkWriter.Threads.cs:NoteAsync"] = "echo: the note body the caller just sent",
        ["Writing/WorkWriter.Threads.cs:AssumeAsync"] = "echo: the statement the caller just sent",
        ["Writing/WorkWriter.Threads.cs:FindingAsync"] = "echo: the finding titles the caller just sent",
        ["Writing/WorkWriter.Threads.cs:ResolveAssumptionAsync"] = "echo: the evidence the caller just sent",
        ["Writing/WorkWriter.Structure.cs:CreateAsync"] = "echo: the titles the caller just sent",
        ["Writing/WorkWriter.Items.cs:DeferAsync"] = "echo: the reason the caller just sent",
        ["Writing/WorkWriter.Evidence.cs:VerifyAsync"] = "echo: the result line the caller just sent",
        ["Writing/WorkWriter.cs:Wrote"] = "echo: the reason the caller just sent",
        ["Writing/WorkWriter.Constraints.cs:ConstrainAsync"] = "echo: the rule text the caller just sent",
        ["Writing/WorkWriter.Constraints.cs:RetireAsync"] = "echo: the reason the caller just sent",
    };

    /// <summary>
    /// <c>file</c> + the exact slice → why it is not a cut text. Identifiers only: an abbreviated sha
    /// git resolves, a minted ref, a ref split at its qualifier.
    /// </summary>
    private static readonly (string File, string Slice, string Why)[] Slices =
    [
        ("Rendering/Render.cs", "flat[..(width - 1)]", "Glyph.Row itself"),
        ("Board/BoardReader.Views.cs", "commit[..7]", "abbreviated sha in a row flag; git resolves it"),
        ("Board/BoardReader.cs", "sha[..8]", "abbreviated branch head; git resolves it"),
        ("Queries/BoardQueries.cs", "reference[..slash]", "the qualifier of a qualified ref"),
        ("Writing/WorkWriter.Threads.cs", "reference[..slash]", "the qualifier of a qualified ref"),
        ("Writing/WorkWriter.Threads.cs", "\"[..6]", "a minted assumption ref"),
        ("Writing/WorkWriter.Constraints.cs", "\"[..6]", "a minted constraint ref"),
        ("Writing/WorkWriter.cs", "\"[..8]", "a minted ref"),
        ("Resources/WorkResources.cs", "@ref[..^WorkUris.BriefSuffix.Length]", "a resource URI with /brief stripped"),
    ];

    [Fact]
    public void Clip_is_gone_and_no_other_helper_takes_its_place()
    {
        var hits = Scan().Where(l => l.Text.Contains("Clip(", StringComparison.Ordinal)).ToList();

        Assert.True(hits.Count == 0, Report("Clip( reappeared", hits));
    }

    [Fact]
    public void Glyph_Row_is_called_only_from_the_documented_exceptions()
    {
        var offenders = new List<SourceLine>();

        foreach (var file in Files())
        {
            var lines = File.ReadAllLines(file.Path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("Glyph.Row(", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = $"{file.Name}:{MethodAt(lines, i)}";
                if (!RowCallers.ContainsKey(key))
                {
                    offenders.Add(new SourceLine(key, i + 1, lines[i].Trim()));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            Report("Glyph.Row called outside the allow-list (docs/workstate/06-rendering.md, \"What may be shortened\")", offenders));
    }

    [Fact]
    public void Every_allow_listed_row_caller_still_exists()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files())
        {
            var lines = File.ReadAllLines(file.Path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Glyph.Row(", StringComparison.Ordinal))
                {
                    found.Add($"{file.Name}:{MethodAt(lines, i)}");
                }
            }
        }

        Assert.Empty(RowCallers.Keys.Except(found));
    }

    [Fact]
    public void No_string_is_cut_by_hand_outside_Glyph_Row()
    {
        var offenders = Scan()
            .Where(l => HandCut().IsMatch(l.Text) || Slice().IsMatch(l.Text))
            .Where(l => !Slices.Any(s => l.File == s.File && l.Text.Contains(s.Slice, StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0, Report("a string is cut outside Glyph.Row", offenders));
    }

    /// <summary>
    /// Slicing by range (<c>text[..n]</c>), Substring, Postgres <c>left(</c> in a query, or an ellipsis
    /// glued to a string — the four shapes every instance so far has taken.
    /// </summary>
    [GeneratedRegex(@"\.Substring\(|\bleft\(|Glyph\.Ellipsis|\+\s*""…""")]
    private static partial Regex HandCut();

    [GeneratedRegex(@"(?<=[\w\)\]""])\[\.\.")]
    private static partial Regex Slice();

    [GeneratedRegex(
        @"^\s*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|partial)\s+)+"
        + @"(?!record\b|class\b|struct\b|interface\b|enum\b)[\w<>\[\]?,.() ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\(")]
    private static partial Regex MethodDeclaration();

    private static string MethodAt(string[] lines, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            var match = MethodDeclaration().Match(lines[i]);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return "?";
    }

    private sealed record SourceFile(string Path, string Name);

    private sealed record SourceLine(string File, int Line, string Text);

    private static IEnumerable<SourceLine> Scan()
        => Files().SelectMany(f => File.ReadAllLines(f.Path).Select((text, i) => new SourceLine(f.Name, i + 1, text)));

    private static string Report(string what, IEnumerable<SourceLine> lines)
        => $"{what}:\n" + string.Join("\n", lines.Select(l => $"  {l.File}:{l.Line}  {l.Text.Trim()}"));

    /// <summary>The three server projects. Migrations are generated and never render text.</summary>
    private static IEnumerable<SourceFile> Files()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WorkState.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var separator = Path.DirectorySeparatorChar;

        foreach (var project in new[] { "WorkState.Core", "WorkState.Api", "WorkState.Mcp" })
        {
            var projectRoot = Path.Combine(root.FullName, project);
            foreach (var path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                    || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                    || path.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return new SourceFile(path, Path.GetRelativePath(projectRoot, path).Replace('\\', '/'));
            }
        }
    }
}

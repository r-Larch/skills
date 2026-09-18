using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkState.Import.Extraction;
using WorkState.Import.Markdown;
using WorkState.Import.Model;

namespace WorkState.Import;

public sealed record RunDirectory(string ProjectSlug, string RepoRoot, string RunSlug, string Path);

/// <summary>
/// Finds the orchestration runs under a path and reads each one into a <see cref="RunImport"/>.
/// UTF-8 in and out, deliberately: the corpus carries ⚠, box drawing, Italian and German, and a
/// mojibaked block would be a real defect rather than a cosmetic one.
/// </summary>
public static class CorpusScanner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] ExcludedSegments = ["worktrees", "node_modules", "bin", "obj", ".git"];

    /// <summary>Measurement output, not knowledge: an artifact row pointing at a path (owner decision D2).</summary>
    private static readonly string[] ArtifactExtensions = [".log", ".json", ".sh", ".csv", ".txt", ".ps1", ".sql"];

    public static IReadOnlyList<RunDirectory> Discover(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var runs = new List<RunDirectory>();

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                $"'{path}' is not a directory. Pass a .claude/orchestration/<slug> run directory, a repo "
                + "root, or a directory of repo roots.");
        }

        if (IsRunDirectory(full) || (!IsRepository(full) && !IsOrchestrationDirectory(full)
                                                        && !HasRepositoryChildren(full) && HoldsMarkdown(full)))
        {
            runs.Add(Single(full));
            return runs;
        }

        if (IsOrchestrationDirectory(full))
        {
            runs.AddRange(RunsUnder(full));
            return runs;
        }

        foreach (var repo in Repositories(full))
        {
            runs.AddRange(RunsUnder(System.IO.Path.Combine(repo, ".claude", "orchestration")));
        }

        return runs;
    }

    private static IEnumerable<RunDirectory> RunsUnder(string orchestration)
    {
        var repo = Directory.GetParent(Directory.GetParent(orchestration)!.FullName)!.FullName;

        return Directory.GetDirectories(orchestration)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(runPath => new RunDirectory(
                System.IO.Path.GetFileName(repo),
                repo,
                System.IO.Path.GetFileName(runPath),
                runPath));
    }

    private static bool IsRunDirectory(string path)
    {
        var parent = Directory.GetParent(path);
        return parent is not null
               && parent.Name.Equals("orchestration", StringComparison.OrdinalIgnoreCase)
               && parent.Parent?.Name.Equals(".claude", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// A <c>.claude/orchestration</c> directory named directly, which is how a corpus is reached when
    /// only that subtree is readable rather than the whole repo.
    /// </summary>
    private static bool IsOrchestrationDirectory(string path)
        => System.IO.Path.GetFileName(path).Equals("orchestration", StringComparison.OrdinalIgnoreCase)
           && Directory.GetParent(path)?.Name.Equals(".claude", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// One run named directly. The project is whatever encloses the run: the repo above
    /// <c>.claude/orchestration</c> where that is the layout, and the parent directory otherwise, so a
    /// run kept outside a repo — a fixture, an archived copy — still imports under a stable project ref.
    /// </summary>
    private static RunDirectory Single(string runPath)
    {
        var repo = IsRunDirectory(runPath)
            ? Directory.GetParent(Directory.GetParent(Directory.GetParent(runPath)!.FullName)!.FullName)!.FullName
            : Directory.GetParent(runPath)!.FullName;

        return new RunDirectory(
            System.IO.Path.GetFileName(repo),
            repo,
            System.IO.Path.GetFileName(runPath),
            runPath);
    }

    private static bool IsRepository(string path)
        => Directory.Exists(System.IO.Path.Combine(path, ".claude", "orchestration"));

    private static bool HasRepositoryChildren(string path)
        => Directory.EnumerateDirectories(path).Any(IsRepository);

    private static bool HoldsMarkdown(string path)
        => Directory.EnumerateFiles(path, "*.md", SearchOption.TopDirectoryOnly).Any();

    /// <summary>The path itself if it is a repo, else every immediate child that is one.</summary>
    private static IEnumerable<string> Repositories(string path)
    {
        if (IsRepository(path))
        {
            yield return path;
            yield break;
        }

        foreach (var child in Directory.GetDirectories(path).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (ExcludedSegments.Any(s => System.IO.Path.GetFileName(child).Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (IsRepository(child))
            {
                yield return child;
            }
        }
    }

    public static RunImport Read(RunDirectory directory)
    {
        var run = new RunImport
        {
            ProjectSlug = directory.ProjectSlug,
            RepoRoot = directory.RepoRoot,
            RunSlug = directory.RunSlug,
            RunPath = directory.Path,
        };

        var parser = new RunParser(run);

        foreach (var file in Files(directory.Path).OrderBy(ReadOrder).ThenBy(f => f, StringComparer.Ordinal))
        {
            var relative = System.IO.Path.GetRelativePath(directory.Path, file).Replace('\\', '/');
            var name = System.IO.Path.GetFileName(file);

            if (name.Equals("SPEC.md", StringComparison.OrdinalIgnoreCase))
            {
                // doc 00 §7: the spec stays in git and is referenced by path + sha. Importing it would
                // recreate the two-artifacts-that-disagree failure this store exists to remove.
                run.FilesSkipped++;
                run.Artifacts.Add(new ArtifactImport(
                    "",
                    "file",
                    relative,
                    JsonSerializer.Serialize(new { role = "spec", sha256 = Sha256(file), bytes = new FileInfo(file).Length })));
                continue;
            }

            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                run.FilesSkipped++;
                run.Artifacts.Add(new ArtifactImport(
                    "",
                    ArtifactKind(name),
                    relative,
                    JsonSerializer.Serialize(new { bytes = new FileInfo(file).Length })));
                continue;
            }

            run.FilesSeen++;
            var (text, removed) = MdText.Storable(File.ReadAllText(file, Utf8));
            if (removed > 0)
            {
                run.ControlChars += removed;
                run.Unmapped.Record(
                    "control-character-postgres-cannot-store",
                    removed + " control character(s) removed to make the file storable",
                    relative,
                    relative);
            }

            var markdown = MarkdownFile.Parse(text, System.IO.Path.GetFileNameWithoutExtension(file));
            parser.AddFile(relative, markdown.Title, markdown);
        }

        parser.ResolveParents();
        return run;
    }

    /// <summary>
    /// Plans before ledgers before briefs. The order is not cosmetic: a plan row carries the verify
    /// command a ledger row's result line needs, and a brief needs its task to exist.
    /// </summary>
    private static int ReadOrder(string file)
    {
        var name = System.IO.Path.GetFileName(file).ToLowerInvariant();
        if (name.StartsWith("plan", StringComparison.Ordinal) || name.StartsWith("tasks", StringComparison.Ordinal))
        {
            return 0;
        }

        if (name.StartsWith("conventions", StringComparison.Ordinal))
        {
            return 1;
        }

        if (name.StartsWith("ledger", StringComparison.Ordinal))
        {
            return 2;
        }

        return name.StartsWith("brief", StringComparison.Ordinal) ? 4 : 3;
    }

    private static string ArtifactKind(string name)
        => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? "log"
            : name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "measurement"
            : "file";

    private static IEnumerable<string> Files(string runPath)
        => Directory.EnumerateFiles(runPath, "*", SearchOption.AllDirectories)
            .Where(f => !Excluded(runPath, f))
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        || ArtifactExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    private static bool Excluded(string runPath, string file)
        => System.IO.Path.GetRelativePath(runPath, file)
            .Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

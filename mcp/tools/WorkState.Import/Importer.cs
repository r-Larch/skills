using WorkState.Import.Model;
using WorkState.Import.Writing;

namespace WorkState.Import;

public sealed record ImportOutcome(IReadOnlyList<RunWriteResult> Runs, UnmappedLog Unmapped)
{
    public int Files => Runs.Sum(r => r.Files);
    public int FilesSkipped => Runs.Sum(r => r.FilesSkipped);
    public int Docs => Runs.Sum(r => r.Docs);
    public int Blocks => Runs.Sum(r => r.Blocks);
    public int Items => Runs.Sum(r => r.Items);
    public int Assumptions => Runs.Sum(r => r.Assumptions);
    public int Notes => Runs.Sum(r => r.Notes);
    public int Constraints => Runs.Sum(r => r.Constraints);
    public int Verifications => Runs.Sum(r => r.Verifications);
    public int Artifacts => Runs.Sum(r => r.Artifacts);
    public int Events => Runs.Sum(r => r.Events);
    public int Attempts => Runs.Sum(r => r.Attempts);
    public int ReplacementChars => Runs.Sum(r => r.ReplacementChars);
    public int ControlChars => Runs.Sum(r => r.ControlChars);
}

public sealed class Importer(string connectionString, string workspaceSlug = "import")
{
    public async Task<ImportOutcome> ImportAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        var writer = new StoreWriter(connectionString, workspaceSlug);
        var results = new List<RunWriteResult>();
        var unmapped = new UnmappedLog();

        foreach (var root in roots)
        {
            foreach (var directory in CorpusScanner.Discover(root))
            {
                var run = CorpusScanner.Read(directory);
                unmapped.Absorb(run.Unmapped);
                results.Add(await writer.WriteAsync(run, cancellationToken));
            }
        }

        return new ImportOutcome(results, unmapped);
    }
}

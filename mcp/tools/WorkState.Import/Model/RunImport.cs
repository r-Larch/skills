using WorkState.Core.Domain;

namespace WorkState.Import.Model;

public sealed class DocImport
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string RelativePath { get; init; }
    public List<BlockImport> Blocks { get; } = [];
    public int ReplacementChars { get; set; }
}

public sealed record BlockImport(string ShortId, decimal Seq, string? Heading, short? HeadingLevel, string Body);

/// <summary>
/// One item as the corpus describes it, before it meets the tree. Several files describe the same
/// <see cref="Ref"/> — a plan proposes it, a ledger closes it — so the fields merge rather than
/// overwrite, and the later, better-evidenced value wins per <see cref="Merge"/>.
/// </summary>
public sealed class ItemImport
{
    public required string Ref { get; init; }
    public required string Kind { get; set; }
    public string? ParentRef { get; set; }
    public string Title { get; set; } = "";
    public string? Intent { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Open;
    public ItemResolution? Resolution { get; set; }
    public string? VerifyCmd { get; set; }
    public string? GateCriterion { get; set; }
    public string? Severity { get; set; }
    public string? Source { get; set; }
    public List<string> RiskTags { get; } = [];
    public List<string> Criteria { get; } = [];
    public int Order { get; set; }

    public void Merge(ItemImport other)
    {
        if (Title.Length == 0 || (other.Title.Length > Title.Length && Title.Length < 12))
        {
            Title = other.Title;
        }

        Intent ??= other.Intent;
        VerifyCmd ??= other.VerifyCmd;
        GateCriterion ??= other.GateCriterion;
        Severity ??= other.Severity;
        Source ??= other.Source;
        ParentRef ??= other.ParentRef;

        if (other.Status != ItemStatus.Open)
        {
            Status = other.Status;
            Resolution = other.Resolution;
        }

        foreach (var tag in other.RiskTags.Where(tag => !RiskTags.Contains(tag)))
        {
            RiskTags.Add(tag);
        }

        foreach (var criterion in other.Criteria.Where(c => !Criteria.Contains(c)))
        {
            Criteria.Add(criterion);
        }
    }
}

public sealed record AssumptionImport(
    string Ref,
    string Statement,
    EvidenceLevel Evidence,
    ReversalCost Reversal,
    AssumptionState State,
    string? RevisitRef,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

public sealed record NoteImport(NoteKind Kind, string Body, string? FromRef);

public sealed record ConstraintImport(ConstraintKind Kind, string Text, string? Route, IReadOnlyList<string> CoversRisk);

public sealed record VerificationImport(
    string ItemRef,
    string Command,
    string ResultLine,
    bool Passed,
    VerifyRunner RunBy,
    string? RedCheckJson);

public sealed record ArtifactImport(string ItemRef, string Kind, string Value, string MetaJson);

public sealed record EventImport(string? ItemRef, string Kind, ActorRole Role, string Actor, string Summary, string PayloadJson);

public sealed record AttemptImport(string ItemRef, string DocSlug, string? AgentName);

/// <summary>
/// The parse-failure ledger. Every shape the importer met and could not type is counted here, and the
/// count is half the deliverable (brief §2, RECON-CORPUS §6).
/// </summary>
public sealed class UnmappedLog
{
    private readonly Dictionary<string, UnmappedShape> _shapes = [];

    public IReadOnlyCollection<UnmappedShape> Shapes => _shapes.Values;

    public void Record(string category, string shape, string sample, string file)
    {
        var key = category + "\u0000" + shape;
        if (!_shapes.TryGetValue(key, out var entry))
        {
            entry = new UnmappedShape(category, shape, sample);
            _shapes[key] = entry;
        }

        entry.Count++;
        entry.Files.Add(file);
    }

    public void Absorb(UnmappedLog other)
    {
        foreach (var shape in other.Shapes)
        {
            var key = shape.Category + "\u0000" + shape.Shape;
            if (!_shapes.TryGetValue(key, out var entry))
            {
                entry = new UnmappedShape(shape.Category, shape.Shape, shape.Sample);
                _shapes[key] = entry;
            }

            entry.Count += shape.Count;
            entry.Files.UnionWith(shape.Files);
        }
    }
}

public sealed class UnmappedShape(string category, string shape, string sample)
{
    public string Category { get; } = category;
    public string Shape { get; } = shape;
    public string Sample { get; } = sample;
    public int Count { get; set; }
    public HashSet<string> Files { get; } = [];
}

/// <summary>Everything one <c>.claude/orchestration/&lt;slug&gt;/</c> directory turned into.</summary>
public sealed class RunImport
{
    public required string ProjectSlug { get; init; }
    public required string RepoRoot { get; init; }
    public required string RunSlug { get; init; }
    public required string RunPath { get; init; }
    public string Goal { get; set; } = "";

    public List<DocImport> Docs { get; } = [];
    public Dictionary<string, ItemImport> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AssumptionImport> Assumptions { get; } = [];
    public List<NoteImport> Notes { get; } = [];
    public List<ConstraintImport> Constraints { get; } = [];
    public List<VerificationImport> Verifications { get; } = [];
    public List<ArtifactImport> Artifacts { get; } = [];
    public List<EventImport> Events { get; } = [];
    public List<AttemptImport> Attempts { get; } = [];
    public UnmappedLog Unmapped { get; } = new();

    public int FilesSeen { get; set; }
    public int FilesSkipped { get; set; }
    public int ReplacementChars { get; set; }
    public int ControlChars { get; set; }

    public ItemImport Item(string reference, string kind)
    {
        if (Items.TryGetValue(reference, out var existing))
        {
            return existing;
        }

        var created = new ItemImport { Ref = reference, Kind = kind, Order = Items.Count };
        Items[reference] = created;
        return created;
    }
}

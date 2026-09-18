using NpgsqlTypes;

namespace WorkState.Core.Domain;

public class Doc
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ItemId { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public int Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public class DocBlock
{
    public Guid Id { get; set; }
    public Guid DocId { get; set; }
    public decimal Seq { get; set; }

    /// <summary>The stable handle shown inline in reads ('b7').</summary>
    public string ShortId { get; set; } = "";

    public string? Heading { get; set; }
    public short? HeadingLevel { get; set; }
    public string Body { get; set; } = "";

    /// <summary>Bumped by the <c>doc_block_revise</c> trigger, which also files the prior version.</summary>
    public int Revision { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; }
    public NpgsqlTsVector? Search { get; private set; }
}

public class DocBlockRevision
{
    public Guid BlockId { get; set; }
    public int Revision { get; set; }
    public string? Heading { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? Actor { get; set; }
}

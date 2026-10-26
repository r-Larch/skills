namespace WorkState.Core.Queries;

/// <summary>Where an imported item came from, and the importer's reason it is still true.</summary>
public sealed record ImportProvenanceRow(
    Guid ItemId,
    string SourcePath,
    string SourceAnchor,
    string Why,
    string ImportedBy,
    Guid? SessionId,
    DateTimeOffset UpdatedAt)
{
    public string Source => SourceAnchor.Length == 0 ? SourcePath : $"{SourcePath}#{SourceAnchor}";
}

public sealed partial class BoardQueries
{
    public async Task<IReadOnlyList<ImportProvenanceRow>> ImportsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT item_id       AS "ItemId",
                   source_path   AS "SourcePath",
                   source_anchor AS "SourceAnchor",
                   why           AS "Why",
                   imported_by   AS "ImportedBy",
                   session_id    AS "SessionId",
                   updated_at    AS "UpdatedAt"
            FROM   import_record
            WHERE  item_id = ANY(@ids);
            """;

        return await ListAsync<ImportProvenanceRow>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }
}

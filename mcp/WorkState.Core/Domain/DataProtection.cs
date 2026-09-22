namespace WorkState.Core.Domain;

/// <summary>
/// One element of the ASP.NET DataProtection key ring that signs the dashboard cookie (doc 08 §4).
/// <para>
/// Nothing reads this table through EF: the rows are served by <c>PostgresXmlRepository</c> over the
/// same <c>NpgsqlDataSource</c> as every other runtime read. The entity exists so the migration that
/// creates the table is generated from the model like the rest of the schema.
/// </para>
/// </summary>
public class DataProtectionKey
{
    public int Id { get; set; }

    /// <summary>The provider's own label for the element — diagnostic only; it keys nothing.</summary>
    public string? FriendlyName { get; set; }

    public string Xml { get; set; } = "";
}

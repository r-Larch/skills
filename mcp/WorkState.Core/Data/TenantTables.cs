using WorkState.Core.Migrations;

namespace WorkState.Core.Data;

/// <summary>
/// Every tenant table and its policy (doc 02 §15): TenantRls's, and those a later migration created under the same
/// policy. A migration's own list stays what it applied, so a table added later is listed here, not there.
/// </summary>
public static class TenantTables
{
    /// <summary>Tables that carry their workspace: the policy compares the column.</summary>
    public static readonly IReadOnlyDictionary<string, string> Direct =
        TenantRls.Direct.Concat(ProjectMembers.Direct).ToDictionary(t => t.Key, t => t.Value);

    /// <summary>Tables with no workspace of their own: a row is the tenant's when its parent is.</summary>
    public static IReadOnlyDictionary<string, string> ThroughParent => TenantRls.ThroughParent;

    /// <summary>
    /// Tenant tables whose rows belong to a project, and their <c>project_access</c> policy (doc 02 §15b). The
    /// <see cref="ThroughParent"/> tables follow their item through their tenant policy instead.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ByProject => ProjectAccess.Tables;
}

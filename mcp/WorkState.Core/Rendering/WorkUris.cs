namespace WorkState.Core.Rendering;

/// <summary>
/// The <c>work://</c> vocabulary (doc 01 §8). Templates are measured <em>undiscoverable</em> in the
/// client, so a templated URI is only ever reachable when something handed the model the exact
/// string — which is why these builders live beside the renderers and every tool that names an item
/// emits the full URI rather than assuming it can be composed.
/// </summary>
public static class WorkUris
{
    public const string Scheme = "work://";

    /// <summary>The four fixed projections. Registered as exact URIs, so they are listed and readable.</summary>
    public const string Workspace = "work://workspace";

    public const string WorkspaceBrief = "work://workspace/brief";
    public const string Conventions = "work://workspace/conventions";
    public const string Handoff = "work://workspace/handoff";

    public const string WorkspaceTemplate = "work://workspace/{slug}";

    /// <summary>
    /// <c>{+ref}</c> is RFC 6570 reserved expansion, which matches <c>/</c> as well — so a qualified
    /// ref reads back as <c>work://item/entity-store/3.1</c>, the form an agent composes and the form
    /// <see cref="Item"/> prints. A plain <c>{ref}</c> stops at the first slash, which is why
    /// <c>work://item/entity-store/3.1/brief</c> was a 404 (ACCEPTANCE A-4).
    /// </summary>
    public const string ItemTemplate = "work://item/{+ref}";

    public const string ItemBriefTemplate = "work://item/{+ref}/brief";
    public const string DocTemplate = "work://doc/{slug}";

    public const string BriefSuffix = "/brief";

    /// <summary>The attention block of the busiest run, uncapped; the template names a scope.</summary>
    public const string Attention = "work://attention";

    public const string AttentionTemplate = "work://attention/{+scope}";

    public static string AttentionFor(string? scope) => scope is null ? Attention : $"{Attention}/{Escape(scope)}";

    public static string Item(string reference) => $"work://item/{Escape(reference)}";

    public static string ItemBrief(string reference) => $"work://item/{Escape(reference)}{BriefSuffix}";

    public static string Doc(string slug) => $"work://doc/{Uri.EscapeDataString(slug)}";

    /// <summary>
    /// A qualified ref is two path segments, not one escaped string: <c>entity-store%2F3.1</c> is
    /// unreadable in a result and is not what a model composes when it needs one.
    /// </summary>
    private static string Escape(string reference)
        => string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));
}

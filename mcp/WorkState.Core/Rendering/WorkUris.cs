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
    public const string ItemTemplate = "work://item/{ref}";
    public const string ItemBriefTemplate = "work://item/{ref}/brief";
    public const string DocTemplate = "work://doc/{slug}";

    public static string Item(string reference) => $"work://item/{Escape(reference)}";

    public static string ItemBrief(string reference) => $"work://item/{Escape(reference)}/brief";

    public static string Doc(string slug) => $"work://doc/{Escape(slug)}";

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}

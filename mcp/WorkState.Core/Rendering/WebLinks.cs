namespace WorkState.Core.Rendering;

/// <summary>
/// The dashboard address of an item, as <c>work_brief</c> and <c>work_get</c> print it: the server's
/// <c>/r/</c> route over the item's full path, which resolves it the way <c>work_get</c> does and
/// redirects to the page. A guid never appears in it, so the line reads back as the ref it names.
/// </summary>
public static class WebLinks
{
    public const string Route = "/r/";

    public static string Link(string webBase, string fullRef)
        => webBase.TrimEnd('/') + Route + string.Join('/', fullRef.Split('/').Select(Uri.EscapeDataString));
}

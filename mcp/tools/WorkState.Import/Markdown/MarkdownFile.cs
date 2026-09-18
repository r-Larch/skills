using System.Text;

namespace WorkState.Import.Markdown;

/// <summary>One ATX section: its heading, and every line under it up to the next heading, verbatim.</summary>
public sealed record MarkdownBlock(int Index, string? Heading, int? HeadingLevel, string Body, int StartLine);

/// <summary>
/// A markdown file split into blocks and nothing else. The split is lossless: concatenating every
/// block's heading line and body reproduces the file, because an unrecognised shape has to degrade to
/// prose rather than disappear (doc 00 §4).
/// </summary>
public sealed class MarkdownFile
{
    private MarkdownFile(string title, IReadOnlyList<MarkdownBlock> blocks, string text)
    {
        Title = title;
        Blocks = blocks;
        Text = text;
    }

    public string Title { get; }

    public IReadOnlyList<MarkdownBlock> Blocks { get; }

    public string Text { get; }

    /// <summary>U+FFFD in the source: a decode that already went wrong before the importer saw it.</summary>
    public int ReplacementChars => Text.Count(c => c == '�');

    public static MarkdownFile Parse(string text, string fallbackTitle)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<MarkdownBlock>();

        string? heading = null;
        int? level = null;
        var startLine = 1;
        var body = new StringBuilder();
        var fence = (string?)null;
        string? title = null;

        void Flush(int index)
        {
            if (heading is null && body.ToString().Trim().Length == 0)
            {
                return;
            }

            blocks.Add(new MarkdownBlock(index, heading, level, body.ToString().TrimEnd('\n'), startLine));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fenceMarker = FenceMarker(line);

            if (fence is not null)
            {
                if (fenceMarker is not null && line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }

                body.Append(line).Append('\n');
                continue;
            }

            if (fenceMarker is not null)
            {
                fence = fenceMarker;
                body.Append(line).Append('\n');
                continue;
            }

            var atx = AtxHeading(line);
            if (atx is null)
            {
                body.Append(line).Append('\n');
                continue;
            }

            Flush(blocks.Count);
            body.Clear();

            (level, heading) = atx.Value;
            startLine = i + 1;
            title ??= level == 1 ? heading : null;
        }

        Flush(blocks.Count);

        return new MarkdownFile(title ?? fallbackTitle, blocks, text);
    }

    private static string? FenceMarker(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return "```";
        }

        return trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : null;
    }

    private static (int Level, string Text)? AtxHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
        {
            return null;
        }

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes > 6 || hashes >= trimmed.Length || trimmed[hashes] != ' ')
        {
            return null;
        }

        return (hashes, trimmed[(hashes + 1)..].Trim().TrimEnd('#').Trim());
    }
}

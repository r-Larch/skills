using System.Text.RegularExpressions;
using Xunit;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 06 §6 and doc 04 §3, as an assertion rather than an intention: every <c>✗</c> is followed by
/// a <c>→</c> that is a callable line. An error that states a problem without naming the fix is a
/// defect, so this runs over <b>every</b> refusal the guards can emit, not over a chosen one.
/// </summary>
internal static partial class RefusalShape
{
    [GeneratedRegex(@"→\s+work_[a-z_]+\(", RegexOptions.Compiled)]
    private static partial Regex CallableLine();

    public static void SelfCorrecting(string text)
    {
        Assert.StartsWith("REFUSED", text, StringComparison.Ordinal);

        var lines = text.Split('\n');
        var conditions = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('✗'))
            {
                continue;
            }

            conditions++;
            var routed = false;

            for (var j = i + 1; j < lines.Length && !lines[j].TrimStart().StartsWith('✗'); j++)
            {
                if (CallableLine().IsMatch(lines[j]))
                {
                    routed = true;
                    break;
                }
            }

            Assert.True(routed, $"condition without a callable route:\n{lines[i]}\n\nin:\n{text}");
        }

        Assert.True(conditions > 0, $"a refusal that lists no condition:\n{text}");
    }

    public static int Conditions(string text)
        => text.Split('\n').Count(l => l.TrimStart().StartsWith('✗'));

    public static void NamesTheOverride(string text)
    {
        Assert.Contains("force:true", text, StringComparison.Ordinal);
        Assert.Contains("forced_transition", text, StringComparison.Ordinal);
    }
}

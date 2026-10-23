using System.Text.RegularExpressions;
using WorkState.Api.App.V1;
using WorkState.Core.Domain;
using Xunit;

namespace WorkState.Tests.App.V1;

/// <summary>
/// doc 10 rule 6, read from the frozen document itself: every <see cref="ItemStatus"/> and
/// <see cref="ItemResolution"/> maps to exactly the string the contract lists, and the contract lists
/// nothing the enum lacks. Adding an enum value without a doc change fails here.
/// </summary>
public sealed partial class V1ContractTests
{
    [GeneratedRegex(@"`([a-z_]+(?:\|[a-z_]+)+)`")]
    private static partial Regex PipeList();

    [Fact]
    public void Every_status_maps_to_the_string_rule_6_documents()
    {
        var documented = RuleSixLists()[0];

        var mapped = Enum.GetValues<ItemStatus>().Select(V1Text.Status).ToList();

        Assert.Equal(documented, mapped);
    }

    [Fact]
    public void Every_resolution_maps_to_the_string_rule_6_documents()
    {
        var documented = RuleSixLists()[1];

        var mapped = Enum.GetValues<ItemResolution>().Select(r => V1Text.Resolution(r)).ToList();

        Assert.Equal(documented, mapped);
    }

    [Fact]
    public void A_seq_prints_as_the_plan_writes_it()
    {
        Assert.Equal("2", V1Text.Seq(2.000m));
        Assert.Equal("1.5", V1Text.Seq(1.50m));
    }

    private static IReadOnlyList<IReadOnlyList<string>> RuleSixLists()
    {
        var document = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "10-read-api.md"));

        var start = document.IndexOf("6. **Enums", StringComparison.Ordinal);
        var end = document.IndexOf("## Shared shapes", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "doc 10 no longer has rule 6 before the shared shapes");

        var lists = PipeList().Matches(document[start..end]).Select(m => (IReadOnlyList<string>)m.Groups[1].Value.Split('|')).ToList();
        Assert.Equal(2, lists.Count);
        return lists;
    }
}

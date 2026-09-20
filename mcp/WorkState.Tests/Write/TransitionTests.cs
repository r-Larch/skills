using WorkState.Core.Domain;
using WorkState.Core.Writing;
using Xunit;

namespace WorkState.Tests.Write;

/// <summary>doc 07 §2 — the table, and the two rows of it that are refusals rather than moves.</summary>
public sealed class TransitionTests
{
    [Theory]
    [InlineData(ItemStatus.Open, Move.Ready)]
    [InlineData(ItemStatus.Open, Move.InProgress)]
    [InlineData(ItemStatus.Ready, Move.InProgress)]
    [InlineData(ItemStatus.InProgress, Move.Review)]
    [InlineData(ItemStatus.InProgress, Move.Done)]
    [InlineData(ItemStatus.Review, Move.Done)]
    [InlineData(ItemStatus.Review, Move.InProgress)]
    [InlineData(ItemStatus.Blocked, Move.InProgress)]
    [InlineData(ItemStatus.Open, Move.Cancelled)]
    [InlineData(ItemStatus.InProgress, Move.Deferred)]
    public void The_table_allows_what_doc_07_draws(ItemStatus from, Move to)
        => Assert.True(Transitions.Legal(from, to), $"{from} → {to} should be legal");

    [Theory]
    [InlineData(ItemStatus.Open, Move.Done)]
    [InlineData(ItemStatus.Open, Move.Review)]
    [InlineData(ItemStatus.Ready, Move.Done)]
    [InlineData(ItemStatus.Done, Move.InProgress)]
    [InlineData(ItemStatus.Cancelled, Move.Ready)]
    [InlineData(ItemStatus.Superseded, Move.InProgress)]
    [InlineData(ItemStatus.InProgress, Move.Superseded)]
    public void And_refuses_what_it_does_not(ItemStatus from, Move to)
        => Assert.False(Transitions.Legal(from, to), $"{from} → {to} should be refused");

    [Fact]
    public void A_refusal_names_what_is_reachable_from_here_rather_than_only_what_is_not()
    {
        var text = Transitions.Refuse("2.4", ItemStatus.Open, Move.Done);

        RefusalShape.SelfCorrecting(text);
        Assert.Contains("legal from here: ready, in_progress", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopening_a_terminal_item_is_refused_with_the_two_ways_to_carry_the_work_forward()
    {
        var text = Transitions.Refuse("2.4", ItemStatus.Done, Move.InProgress);

        RefusalShape.SelfCorrecting(text);
        Assert.Contains("terminal", text, StringComparison.Ordinal);
        Assert.Contains("work_create(", text, StringComparison.Ordinal);
        Assert.Contains("supersedes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Superseded_is_never_a_direct_move_and_the_refusal_names_the_link_that_sets_it()
    {
        var text = Transitions.Refuse("2.4", ItemStatus.InProgress, Move.Superseded);

        RefusalShape.SelfCorrecting(text);
        Assert.Contains("work_link(from:", text, StringComparison.Ordinal);
        Assert.Contains("type:\"supersedes\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal quotes the status the caller can actually type, not the board's nine-wide
    /// abbreviation: <c>in_progress</c>, never <c>doing</c>.
    /// </summary>
    [Fact]
    public void A_refusal_quotes_the_status_work_status_accepts()
    {
        var text = Transitions.Refuse("2.4", ItemStatus.InProgress, Move.Ready);

        Assert.Contains("2.4 is in_progress", text, StringComparison.Ordinal);
        Assert.DoesNotContain("doing", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("in_progress", Move.InProgress)]
    [InlineData("IN_PROGRESS", Move.InProgress)]
    [InlineData("done", Move.Done)]
    [InlineData("deferred", Move.Deferred)]
    public void The_move_vocabulary_parses_the_way_an_agent_types_it(string typed, Move expected)
    {
        Assert.True(Transitions.TryParse(typed, out var move));
        Assert.Equal(expected, move);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("")]
    [InlineData(null)]
    public void And_refuses_a_word_that_is_not_a_move(string? typed)
        => Assert.False(Transitions.TryParse(typed, out _));
}

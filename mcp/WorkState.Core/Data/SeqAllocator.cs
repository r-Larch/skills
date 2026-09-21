namespace WorkState.Core.Data;

/// <summary>
/// Fractional ordering keys for siblings (doc 02 §3). Inserting between two neighbours writes ONE
/// row instead of renumbering everything below it, which is what a markdown plan cannot do.
/// </summary>
public static class SeqAllocator
{
    public const decimal Step = 1m;

    /// <summary>
    /// The key for a new sibling landing between <paramref name="before"/> and
    /// <paramref name="after"/>; either bound may be null for "at the start" / "at the end".
    /// </summary>
    public static decimal Between(decimal? before, decimal? after)
    {
        if (before is { } lo && after is { } hi && lo >= hi)
        {
            throw new ArgumentException(
                $"seq bounds are out of order: before={lo} is not below after={hi}. " +
                "Pass the two neighbours in sibling order (ORDER BY seq).",
                nameof(before));
        }

        return (before, after) switch
        {
            (null, null) => Step,
            (null, { } first) => first - Step,
            ({ } last, null) => last + Step,
            ({ } l, { } h) => Midpoint(l, h),
        };
    }

    private static decimal Midpoint(decimal lo, decimal hi)
    {
        var mid = lo + ((hi - lo) / 2m);

        if (mid <= lo || mid >= hi)
        {
            throw new InvalidOperationException(
                $"no representable seq between {lo} and {hi}: this branch of the sibling list needs " +
                "rebalancing (rewrite the siblings' seq as 1, 2, 3 … in their current order) before " +
                "another insert can land here.");
        }

        return mid;
    }
}

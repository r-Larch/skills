using System.Globalization;
using System.Text;
using WorkState.Core.Domain;

namespace WorkState.Api.App.V1;

/// <summary>
/// doc 10 rule 6. <see cref="Status"/> and <see cref="Resolution"/> are spelled out rather than
/// derived: an enum value added without a doc change must fail a test, not ship a new string.
/// </summary>
public static class V1Text
{
    public static string Status(ItemStatus status) => status switch
    {
        ItemStatus.Open => "open",
        ItemStatus.Ready => "ready",
        ItemStatus.InProgress => "in_progress",
        ItemStatus.Blocked => "blocked",
        ItemStatus.Review => "review",
        ItemStatus.Done => "done",
        ItemStatus.Cancelled => "cancelled",
        ItemStatus.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "not in doc 10 rule 6"),
    };

    public static string Resolution(ItemResolution resolution) => resolution switch
    {
        ItemResolution.Accepted => "accepted",
        ItemResolution.Reworked => "reworked",
        ItemResolution.Replanned => "replanned",
        ItemResolution.Escalated => "escalated",
        ItemResolution.Skipped => "skipped",
        ItemResolution.Wontfix => "wontfix",
        ItemResolution.Duplicate => "duplicate",
        ItemResolution.Obsolete => "obsolete",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "not in doc 10 rule 6"),
    };

    public static string? Resolution(ItemResolution? resolution) => resolution is { } value ? Resolution(value) : null;

    /// <summary>The store's own label for every other enum: <c>DoNotRederive</c> is <c>do_not_rederive</c>.</summary>
    public static string Snake(Enum value)
    {
        var pascal = value.ToString();
        var builder = new StringBuilder(pascal.Length + 4);

        foreach (var character in pascal)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>A seq as the plan writes it: <c>1.5</c>, <c>2</c> — never <c>2.000</c>.</summary>
    public static string Seq(decimal seq) => seq.ToString("0.############################", CultureInfo.InvariantCulture);
}

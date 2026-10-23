namespace WorkState.Api.App;

/// <summary>
/// The honesty strip of doc 09 §5 — one line, four counts, and the reason the product exists.
/// Not a chart: a count that should be zero and usually is not. Part of v1's overview (doc 10).
/// </summary>
public sealed record HonestyView(int DoneUnverified, int Forced, int Ungated, int FollowupsOpen);

public sealed record SessionRequest(string? Username, string? Password);

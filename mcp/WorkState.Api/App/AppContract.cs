namespace WorkState.Api.App;

/// <summary>
/// The honesty strip of doc 09 §5 — one line, four counts, and the reason the product exists.
/// Not a chart: a count that should be zero and usually is not. Part of v1's overview (doc 10).
/// </summary>
public sealed record HonestyView(int DoneUnverified, int Forced, int Ungated, int FollowupsOpen);

public sealed record SessionRequest(string? Username, string? Password);

/// <summary>A workspace the signed-in user may enter. <c>Current</c> is the one the cookie acts in.</summary>
public sealed record SessionTenantView(Guid Id, string Slug, string Role, bool Current);

public sealed record SwitchTenantRequest(Guid? Id);

public sealed record ChangePasswordRequest(string? Current, string? Next);

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkState.Api.Auth;

/// <summary>
/// What a new name, password or workspace slug must be, in one place: invite accept, password change and reset
/// all ask here, and the dashboard repeats the same rules for its hints (<c>frontend/src/admin.ts</c>).
/// Each answer is null when the value is fine, otherwise the sentence to show.
/// </summary>
public static partial class AccountRules
{
    public const int PasswordMinLength = 12;

    /// <summary>A bound on what one hash verification is asked to read, not a strength rule.</summary>
    public const int PasswordMaxLength = 1024;

    public const int NameMaxLength = 64;

    public const int SlugMaxLength = 63;

    public static string? PasswordProblem(string? password)
        => password is null || password.Length < PasswordMinLength
            ? $"A password needs at least {PasswordMinLength} characters."
            : password.Length > PasswordMaxLength
                ? $"A password has at most {PasswordMaxLength} characters."
                : null;

    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "A name is required: it is what you sign in with.";
        }

        if (trimmed.Length > NameMaxLength)
        {
            return $"A name has at most {NameMaxLength} characters.";
        }

        return trimmed.Any(char.IsControl) ? "A name cannot contain control characters." : null;
    }

    /// <summary>The shape of <c>default</c>: lowercase letters and digits, words joined by single hyphens.</summary>
    public static string? SlugProblem(string? slug)
        => slug is not null && slug.Length <= SlugMaxLength && Slug().IsMatch(slug)
            ? null
            : $"A workspace slug is lowercase letters and digits, words joined by single hyphens, at most {SlugMaxLength} "
              + "characters — like default or team-two.";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();
}

/// <summary>
/// The secret in an invite or reset link: 32 random bytes, base64url, so it sits in a URL path as it is. Only
/// <see cref="Hash"/> of it is stored (C64), and the create response is the one place it is ever written.
/// </summary>
public static class LinkToken
{
    /// <summary>32 bytes in unpadded base64url.</summary>
    public const int Length = 43;

    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 over the token's UTF-8 bytes, as lowercase hex — <c>SessionToken.Hash</c>'s form.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Whether it could be one at all; anything else is refused without a query.</summary>
    public static bool Plausible(string? token)
        => token is { Length: Length } && Base64Url.IsValid(token);
}

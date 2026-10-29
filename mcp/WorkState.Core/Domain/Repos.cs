using System.Text.RegularExpressions;

namespace WorkState.Core.Domain;

/// <summary>
/// One project per repository (doc 02 §project_repo). A repository is known by up to two keys, and
/// either one matching is enough: its normalised <c>origin</c> remote, and its main checkout's root.
/// A project may hold several repositories; a remote belongs to one project per workspace, a root to one project per
/// workspace and user, because a path names a folder on one person's machine.
/// </summary>
public class ProjectRepo
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectItemId { get; set; }

    /// <summary>
    /// Whose machine a <see cref="RepoKeys.Root"/> key is a path on: the binder's user, null for a credential that has
    /// none. A remote names one repository for everyone, so it is always null.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary><see cref="RepoKeys.Remote"/> or <see cref="RepoKeys.Root"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Already normalised by <see cref="RepoKeys"/>; the unique key with the workspace and the user.</summary>
    public string Key { get; set; } = "";

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// The identity rule. The server cannot run git, so the client computes both keys and the server
/// normalises whatever it receives. <c>plugins/rlarch/skills/nested-claude/server/repo.mjs</c> holds
/// the same rule, and both test suites run the same table of inputs.
/// </summary>
public static partial class RepoKeys
{
    public const string Remote = "remote";
    public const string Root = "root";

    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^[^/]*@")]
    private static partial Regex User();

    /// <summary>scp form, <c>host:owner/name</c>. A one-letter host is a drive letter and never matches.</summary>
    [GeneratedRegex(@"^([^/:]{2,}):/*")]
    private static partial Regex Scp();

    [GeneratedRegex(@"/*(\.git)?/*$", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteTail();

    [GeneratedRegex(@"/*(/\.git)?/*$", RegexOptions.IgnoreCase)]
    private static partial Regex RootTail();

    [GeneratedRegex(@"^([a-z]:(/|$)|//)", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPath();

    /// <summary>
    /// <c>git@github.com:r-Larch/skills.git</c> and <c>https://github.com/r-Larch/skills</c> are
    /// both <c>github.com/r-larch/skills</c>: no scheme, no <c>user@</c>, the scp form's colon a
    /// slash, no trailing <c>.git</c> or <c>/</c>, lowercase.
    /// </summary>
    public static string? NormaliseRemote(string? remote)
    {
        var value = remote?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var schemed = Scheme().IsMatch(value);
        value = User().Replace(Scheme().Replace(value, string.Empty), string.Empty);
        if (!schemed)
        {
            value = Scp().Replace(value, "$1/");
        }

        value = RemoteTail().Replace(value, string.Empty);
        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    /// <summary>
    /// The main checkout's root, the same for every worktree of the repository: forward slashes, no
    /// trailing slash, a trailing <c>/.git</c> (the common dir itself) dropped, and on a Windows path
    /// the whole path lowercase, because that file system is case-insensitive and git's own output
    /// varies the drive letter's case.
    /// </summary>
    public static string? NormaliseRoot(string? root)
    {
        var value = root?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        value = RootTail().Replace(value, string.Empty);
        if (WindowsPath().IsMatch(value))
        {
            value = value.ToLowerInvariant();
        }

        return value.Length == 0 ? null : value;
    }

    /// <summary>The keys a <c>{remote, root}</c> pair names, normalised; empty when it names neither.</summary>
    public static IReadOnlyList<RepoKey> Of(string? remote, string? root)
    {
        var keys = new List<RepoKey>(2);
        if (NormaliseRemote(remote) is { } r)
        {
            keys.Add(new RepoKey(Remote, r));
        }

        if (NormaliseRoot(root) is { } p)
        {
            keys.Add(new RepoKey(Root, p));
        }

        return keys;
    }

    /// <summary>The user a key is held for: <paramref name="userId"/> for a root, a path on their machine; none for a remote.</summary>
    public static Guid? UserOf(RepoKey key, Guid? userId) => key.Kind == Root ? userId : null;

    /// <summary>The keys as the argument a route prints: <c>{remote:"…", root:"…"}</c>.</summary>
    public static string Literal(IReadOnlyList<RepoKey> keys)
        => "{" + string.Join(", ", keys.Select(k => $"{k.Kind}:\"{k.Key}\"")) + "}";
}

public sealed record RepoKey(string Kind, string Key);

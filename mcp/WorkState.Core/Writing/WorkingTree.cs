using System.Text.RegularExpressions;

namespace WorkState.Core.Writing;

/// <summary>
/// The <c>tree</c> a caller passes to <c>work_status(in_progress)</c>, reduced to one spelling per
/// working tree. A path is a place on one person's disk, so its lock is per workspace and user; a
/// branch is a name, so its lock is per workspace.
/// </summary>
public sealed partial record WorkingTree(string Key, bool IsPath)
{
    public static WorkingTree? Parse(string? tree)
    {
        var value = tree?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // Windows paths compare case-insensitively and take either slash; POSIX paths do neither.
        if (WindowsPath().IsMatch(value))
        {
            var path = value.Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
            return new(path.EndsWith(':') ? path + "/" : path, true);
        }

        if (value.StartsWith('/'))
        {
            var path = value.TrimEnd('/');
            return new(path.Length == 0 ? "/" : path, true);
        }

        return new(value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value, false);
    }

    [GeneratedRegex(@"^([A-Za-z]:([\\/]|$)|\\\\)")]
    private static partial Regex WindowsPath();
}

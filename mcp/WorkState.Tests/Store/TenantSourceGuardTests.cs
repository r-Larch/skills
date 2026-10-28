using Xunit;

namespace WorkState.Tests.Store;

/// <summary>
/// C62 at the source: <c>WorkspaceOptions</c> — <c>WorkState:Workspace</c> — is the static tokens' own binding,
/// read by their handler and registered by the composition root. Any other production file that names it is a
/// request path reaching for a configured default instead of its principal's tenant.
/// </summary>
public sealed class TenantSourceGuardTests
{
    private static readonly string[] Allowed =
    [
        Path.Combine("WorkState.Api", "ApiHost.cs"),
        Path.Combine("WorkState.Api", "Auth", "StaticTokenAuthentication.cs"),
    ];

    [Fact]
    public void Only_the_static_token_binding_and_the_composition_root_name_WorkspaceOptions_not_a_tenant_path()
    {
        var mcp = Root();
        var sources = Directory.EnumerateDirectories(mcp, "WorkState.*")
            .Where(project => !Path.GetFileName(project).Equals("WorkState.Tests", StringComparison.Ordinal))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(sources.Count > 50, $"found only {sources.Count} sources under {mcp}");

        var offenders = sources
            .Where(file => File.ReadAllText(file).Contains("WorkspaceOptions", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(mcp, file))
            .Where(file => !Allowed.Contains(file))
            .ToList();

        Assert.True(offenders.Count == 0, $"WorkspaceOptions outside the static-token binding: {string.Join(", ", offenders)}");
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WorkState.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no WorkState.slnx above {AppContext.BaseDirectory}");
    }
}

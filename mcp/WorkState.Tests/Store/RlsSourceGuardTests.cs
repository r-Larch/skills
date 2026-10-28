using System.Text.RegularExpressions;
using Xunit;

namespace WorkState.Tests.Store;

/// <summary>
/// doc 02 §15 at the source: <c>WorkStateConnections</c> is the only way the server opens a connection or begins a
/// transaction, so a request path cannot reach the database except as the tenant role or through a bypass that
/// names its reason. The EF context is the owner's, and only the schema owner, OpenIddict's own tables and the
/// offline importer build one.
/// </summary>
public sealed partial class RlsSourceGuardTests
{
    private static readonly string Helper = Path.Combine("WorkState.Core", "Data", "WorkStateConnections.cs");

    /// <summary>Who may build an EF context on the connection string, and why it is not a request path.</summary>
    private static readonly string[] OwnerContexts =
    [
        Path.Combine("WorkState.Api", "SchemaOwner.cs"), // migrations, at startup
        Path.Combine("WorkState.Api", "Auth", "OAuth", "OAuthServer.cs"), // OpenIddict's tables, which hold no tenant rows
        Path.Combine("WorkState.Core", "Data", "WorkStateDbContextOptions.cs"),
        Path.Combine("WorkState.Core", "Data", "WorkStateDesignTimeFactory.cs"),
    ];

    [Fact]
    public void Rls_no_connection_or_transaction_is_opened_outside_the_helper()
    {
        var offenders = Sources()
            .SelectMany(file => RawOpen().Matches(File.ReadAllText(file)).Select(match => $"{Relative(file)}: {match.Value}"))
            .Where(hit => !hit.StartsWith(Helper + ":", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"raw connection outside WorkStateConnections:\n{string.Join("\n", offenders)}");
    }

    [Fact]
    public void Rls_only_the_schema_owner_openiddict_and_the_importer_build_an_ef_context()
    {
        var offenders = Sources()
            .Where(file => EfContext().IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Where(file => !OwnerContexts.Contains(file)
                           && !file.StartsWith(Path.Combine("tools", "WorkState.Import") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"an EF context outside the owner's paths: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Rls_nothing_in_the_server_runs_unscoped_callers_as_the_owner()
    {
        var offenders = Sources()
            .Where(file => File.ReadAllText(file).Contains("RunUnscopedAsOwner(", StringComparison.Ordinal))
            .Select(Relative)
            .Where(file => file != Helper)
            .ToList();

        Assert.True(offenders.Count == 0, $"RunUnscopedAsOwner outside the test suite: {string.Join(", ", offenders)}");
    }

    [GeneratedRegex(@"\b(OpenConnection(Async)?|BeginTransaction(Async)?|CreateConnection)\s*\(|[Dd]ataSource\s*\.\s*Create(Command|Batch)\s*\(|new\s+NpgsqlConnection\s*\(")]
    private static partial Regex RawOpen();

    [GeneratedRegex(@"new\s+WorkStateDbContext\s*\(|AddDbContext\s*<\s*WorkStateDbContext|\.UseWorkState\s*\(|WorkStateDbContextOptions\.Build\s*\(")]
    private static partial Regex EfContext();

    private static IEnumerable<string> Sources()
    {
        var mcp = Root();
        var sources = Directory.EnumerateDirectories(mcp, "WorkState.*")
            .Append(Path.Combine(mcp, "tools"))
            .Where(project => !Path.GetFileName(project).Equals("WorkState.Tests", StringComparison.Ordinal))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(sources.Count > 50, $"found only {sources.Count} sources under {mcp}");
        return sources;
    }

    private static string Relative(string file) => Path.GetRelativePath(Root(), file);

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

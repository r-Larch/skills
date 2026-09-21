using WorkState.Core.Board;
using WorkState.Core.Queries;
using WorkState.Tests.Import;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// The composition layer against the store, with no web host in the way — so a failure here points
/// at a query or a renderer rather than at the transport.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class BoardReaderTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_rendering_of_an_imported_run_comes_back_non_empty_and_uuid_free()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run", $"reader-{Guid.CreateVersion7():n}");
        var reader = NewReader();

        var scope = Assert.IsType<ScopeResult.Found>(
            await reader.ResolveAsync(run.WorkspaceSlug, null, Ct));

        Assert.Equal("sample-run", scope.Item.Ref);

        var renderings = new Dictionary<string, string>
        {
            ["brief"] = await reader.BriefAsync(scope, "resume", Ct),
            ["next"] = await reader.NextAsync(scope, 10, null, Ct),
            ["tree"] = await reader.TreeAsync(scope, 3, [], true, Ct),
            ["get"] = await reader.GetAsync(scope, ["T1.1", "phase-1"], ["deps"], Ct),
            ["search"] = await reader.SearchAsync(scope.WorkspaceId, "token", 10, Ct),
            ["threads"] = await reader.ThreadsAsync(scope, [], Ct),
            ["audit"] = await reader.AuditAsync(scope, Ct),
            ["history"] = await reader.HistoryAsync(scope.WorkspaceId, scope, 0, 20, Ct),
            ["conventions"] = await reader.ConventionsAsync(scope, Ct),
        };

        foreach (var (name, text) in renderings)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{name} rendered empty");
            Assert.DoesNotMatch(RendererTests.Uuid, text);
        }
    }

    [Fact]
    public async Task An_unknown_scope_is_refused_with_the_call_that_finds_the_right_one()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run", $"reader-{Guid.CreateVersion7():n}");
        var reader = NewReader();

        var result = await reader.ResolveAsync(run.WorkspaceSlug, "no-such-ref", Ct);

        Assert.IsType<ScopeResult.Missing>(result);
    }

    private BoardReader NewReader()
        => new(new WorkStateQueries(fixture.DataSource), new BoardQueries(fixture.DataSource));
}

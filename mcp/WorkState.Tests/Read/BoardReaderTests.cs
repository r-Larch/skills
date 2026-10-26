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

    /// <summary>
    /// get-full-text. work_get is "full detail" and clipped intent at 86 columns and each criterion
    /// at 80; both now wrap, as the worker brief does. The boards keep one clipped row per item.
    /// </summary>
    [Fact]
    public async Task Work_get_prints_intent_criteria_and_evidence_in_full_and_the_boards_still_clip()
    {
        var world = await Write.WriteSeed.CreateAsync(fixture);
        var intent = Words("intent", 300);
        var criterion = Words("criterion", 200);
        var result = Words("result", 150);
        var title = Words("title", 150);

        await using (var db = fixture.NewContext())
        {
            var task = db.Items.Single(i => i.Id == world.TaskId);
            task.Intent = intent;
            task.Title = title;
            db.Verifications.Add(new WorkState.Core.Domain.Verification
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = world.WorkspaceId,
                ItemId = world.TaskId,
                ResultLine = result,
                Passed = true,
                RunBy = WorkState.Core.Domain.VerifyRunner.Orchestrator,
            });
            await db.SaveChangesAsync(Ct);
        }

        await Write.WriteSeed.CriterionAsync(world, world.TaskId, 1, criterion, ticked: false);

        var home = await world.ScopeAsync("run-a");
        var got = await world.Reader.GetAsync(home, ["1.1"], [], Ct);
        var joined = System.Text.RegularExpressions.Regex.Replace(got, @"\n {11,}", " ");

        Assert.Contains($"  intent    {intent}\n", joined, StringComparison.Ordinal);
        Assert.Contains($"  ✗ c1     {criterion}\n", joined, StringComparison.Ordinal);
        Assert.Contains($"  evidence  ✓ {result} by orchestrator,", joined, StringComparison.Ordinal);
        Assert.All(
            got.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal)),
            line => Assert.True(line.Length <= 100, $"{line.Length} wide: {line}"));

        var tree = await world.Reader.TreeAsync(home, 3, [], true, Ct);
        var next = await world.Reader.NextAsync(home, 10, null, Ct);
        foreach (var board in new[] { tree, next })
        {
            Assert.DoesNotContain(title, board, StringComparison.Ordinal);
            Assert.DoesNotContain(intent[..40], board, StringComparison.Ordinal);
            Assert.Contains(title[..20], board, StringComparison.Ordinal);
            Assert.Contains("…", board, StringComparison.Ordinal);
        }
    }

    /// <summary>Distinct words, so the text survives word-wrapping and a clip is visible.</summary>
    private static string Words(string stem, int length)
    {
        var builder = new System.Text.StringBuilder();
        for (var n = 1; builder.Length < length; n++)
        {
            builder.Append(builder.Length == 0 ? string.Empty : " ").Append(stem).Append(n);
        }

        return builder.ToString()[..length].TrimEnd();
    }

    private BoardReader NewReader()
        => new(new WorkStateQueries(fixture.DataSource), new BoardQueries(fixture.DataSource));
}

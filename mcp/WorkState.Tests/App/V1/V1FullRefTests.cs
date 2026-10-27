using System.Text.Json;
using WorkState.Core.Queries;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.App.V1.V1Http;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// Unit <c>backlog/dash-paths</c>: every ItemRef carries <c>fullRef</c>, the path
/// <c>project/run/ref</c> the dashboard copies, and it resolves to its item alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1FullRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_full_ref_is_the_project_run_ref_path_and_resolves_to_its_item_alone()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var nineOne = await WriteSeed.SecondRunAsync(world, "run-b");

        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        var reader = Reader(server);

        var board = await GetAsync(reader, $"/api/v1/runs/{world.RunId}/board");
        var project = board.GetProperty("project").GetProperty("ref").GetString()!;

        Assert.Equal(project, board.GetProperty("project").GetProperty("fullRef").GetString());
        Assert.Equal($"{project}/run-a", board.GetProperty("run").GetProperty("fullRef").GetString());
        Assert.Equal($"{project}/run-a/1.1", ByRef(Array(board, "rows"), "1.1").GetProperty("item").GetProperty("fullRef").GetString());
        Assert.Equal($"{project}/run-a/1", ByRef(Array(board, "rows"), "1").GetProperty("item").GetProperty("fullRef").GetString());

        // The bare ref is unique here, so qualifiedRef stays short while fullRef is the whole path.
        var task = (await GetAsync(reader, $"/api/v1/items/{world.TaskId}")).GetProperty("item");
        Assert.Equal("1.1", task.GetProperty("qualifiedRef").GetString());
        Assert.Equal($"{project}/run-a/1.1", task.GetProperty("fullRef").GetString());

        var refs = new List<JsonElement>();
        foreach (var item in new[] { world.TaskId, world.PhaseId, world.RunId, world.ProjectId, nineOne })
        {
            var page = await GetAsync(reader, $"/api/v1/items/{item}");
            refs.Add(page.GetProperty("item"));
            refs.AddRange(Array(page, "path"));
        }

        var queries = new BoardQueries(fixture.DataSource);
        var home = await world.ScopeAsync("run-a");
        foreach (var shown in refs)
        {
            var full = shown.GetProperty("fullRef").GetString()!;
            var resolved = await queries.ResolveAsync(world.WorkspaceId, full, Ct);
            Assert.True(resolved.Count == 1, $"{full} resolves to {resolved.Count} items");
            Assert.Equal(shown.GetProperty("id").GetGuid(), resolved[0].Id);

            var printed = await world.Reader.GetAsync(home, [full], [], Ct);
            Assert.Contains(shown.GetProperty("title").GetString()!, printed, StringComparison.Ordinal);
        }

        Assert.Contains(refs, r => r.GetProperty("fullRef").GetString() == $"{project}/run-b/9.1");
    }
}

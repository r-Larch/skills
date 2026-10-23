using System.Text.Json;
using System.Threading.Channels;
using WorkState.Core.Rendering;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.App.V1.V1Http;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// c1: the live data has a run <c>backlog</c> and a phase <c>m0/backlog</c>, and the pre-v1
/// <c>/api/runs/backlog/tree</c> 404'd on exactly that. On v1 a ref is only ever resolved, and every
/// address is an id.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1SharedRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_run_and_a_phase_sharing_a_ref_resolve_to_two_ids_and_each_has_a_board()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Guid backlogRun, m0, backlogPhase;

        await using (var db = fixture.NewContext())
        {
            var run = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", "backlog", 5m, "Backlog");
            var other = Seed.NewItem(world.WorkspaceId, world.ProjectId, "run", "m0", 6m, "Milestone 0");
            db.Items.AddRange(run, other);
            await db.SaveChangesAsync(Ct);

            var phase = Seed.NewItem(world.WorkspaceId, other.Id, "phase", "backlog", 1m, "m0's backlog phase");
            db.Items.Add(phase);
            db.Items.Add(Seed.NewItem(world.WorkspaceId, phase.Id, "task", "b.1", 1m, "carried"));
            await db.SaveChangesAsync(Ct);

            (backlogRun, m0, backlogPhase) = (run.Id, other.Id, phase.Id);
        }

        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        var reader = Reader(server);

        var matches = Array(await GetAsync(reader, "/api/v1/resolve?ref=backlog"), "matches");
        Assert.Equal(2, matches.Count);
        Assert.Equal(
            new HashSet<Guid> { backlogRun, backlogPhase },
            matches.Select(m => m.GetProperty("id").GetGuid()).ToHashSet());

        // Rule 2: each qualifiedRef is the form work_get prints, and it resolves to that item alone.
        var home = await world.ScopeAsync("run-a");
        foreach (var match in matches)
        {
            var qualified = match.GetProperty("qualifiedRef").GetString()!;
            Assert.NotEqual("backlog", qualified);

            var only = Assert.Single(Array(await GetAsync(reader, $"/api/v1/resolve?ref={Uri.EscapeDataString(qualified)}"), "matches"));
            Assert.Equal(match.GetProperty("id").GetGuid(), only.GetProperty("id").GetGuid());

            var printed = await world.Reader.GetAsync(home, [qualified], [], Ct);
            Assert.Contains(WorkUris.Item(qualified), printed, StringComparison.Ordinal);
        }

        var runBoard = await GetAsync(reader, $"/api/v1/runs/{backlogRun}/board");
        Assert.Equal(backlogRun, runBoard.GetProperty("run").GetProperty("id").GetGuid());
        Assert.Equal(backlogRun, Array(runBoard, "rows")[0].GetProperty("item").GetProperty("id").GetGuid());

        var phaseBoard = await GetAsync(reader, $"/api/v1/runs/{backlogPhase}/board");
        Assert.Equal(m0, phaseBoard.GetProperty("run").GetProperty("id").GetGuid());
        var rows = Array(phaseBoard, "rows");
        Assert.Equal(backlogPhase, rows[0].GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("m0/backlog", rows[0].GetProperty("item").GetProperty("qualifiedRef").GetString());
        Assert.Equal("b.1", rows[1].GetProperty("item").GetProperty("ref").GetString());
    }

    /// <summary>The stream keeps its transport and gains the ids a client re-reads by.</summary>
    [Fact]
    public async Task A_stream_frame_names_the_run_and_the_item_it_concerns()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        var reader = Reader(server);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        session.CancelAfter(TimeSpan.FromMinutes(1));

        using var response = await reader.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, session.Token);
        await using var body = await response.Content.ReadAsStreamAsync(session.Token);
        using var lines = new StreamReader(body);

        var frames = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await lines.ReadLineAsync(session.Token) is { } line)
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        frames.Writer.TryWrite(line[6..]);
                    }
                }
            }
            catch (Exception)
            {
                // The response ends when the server is disposed.
            }
        }, session.Token);

        string? frame = null;
        for (var attempt = 0; attempt < 5 && frame is null; attempt++)
        {
            var wrote = await world.Writer.NoteAsync(
                world.WorkspaceId, WriteSeed.Orchestrator, "observation", $"live {attempt}", "1.1", null, Ct);
            Assert.False(wrote.Refused, wrote.Text);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                frame = await frames.Reader.ReadAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.NotNull(frame);
        var data = JsonDocument.Parse(frame!).RootElement;
        Assert.Equal("note", data.GetProperty("kind").GetString());
        Assert.Equal(world.TaskId, data.GetProperty("itemId").GetGuid());
        Assert.Equal(world.RunId, data.GetProperty("runId").GetGuid());
        Assert.True(data.GetProperty("seq").GetInt64() > 0);
    }
}

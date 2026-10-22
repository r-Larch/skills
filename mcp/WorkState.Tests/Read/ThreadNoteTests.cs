using System.Text.RegularExpressions;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// ws/11. work_threads listed notes written with from: and no to: as unclosed, each ending in a
/// work_brief call that could never consume them — only the worker brief of a note's addressee does.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ThreadNoteTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_record_note_is_not_a_thread_and_an_addressed_note_names_the_call_that_closes_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var record = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "decision", "a decision kept for the record", null, "1.1", Ct);
        Assert.False(record.Refused, record.Text);

        var addressed = await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", "a note the task must read", "1.1", null, Ct);
        Assert.False(addressed.Refused, addressed.Text);

        var threads = await world.Reader.ThreadsAsync(await world.ScopeAsync("run-a"), ["notes"], Ct);

        Assert.DoesNotContain("a decision kept for the record", threads, StringComparison.Ordinal);
        Assert.Contains("a note the task must read", threads, StringComparison.Ordinal);

        var hint = Regex.Match(threads, @"work_brief\(scope:""([^""]+)"", for:""worker""\) consumes it");
        Assert.True(hint.Success, threads);

        await using (var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct))
        await using (var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct))
        {
            await ReadServer.CallAsync(
                client,
                "work_brief",
                new Dictionary<string, object?> { ["scope"] = hint.Groups[1].Value, ["for"] = "worker" },
                Ct);
        }

        var after = await world.Reader.ThreadsAsync(await world.ScopeAsync("run-a"), ["notes"], Ct);

        Assert.StartsWith("THREADS  nothing unclosed under run-a.", after, StringComparison.Ordinal);
    }
}

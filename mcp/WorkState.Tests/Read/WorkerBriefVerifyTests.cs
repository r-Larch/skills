using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// The done guard compares the command a verification recorded with the task's verify_cmd,
/// literally. The worker brief printed the criteria and the report format but not the command, so
/// a mismatch stayed invisible until the close was refused.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class WorkerBriefVerifyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_worker_brief_prints_the_tasks_verify_cmd_whole()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        const string command = "dotnet test mcp/WorkState.slnx --filter \"FullyQualifiedName~Guard|FullyQualifiedName~ClaimTreeTests\"";

        await using (var db = fixture.NewContext())
        {
            (await db.Items.FindAsync([world.TaskId], Ct))!.VerifyCmd = command;
            await db.SaveChangesAsync(Ct);
        }

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "worker", Ct);
        var lines = brief.Split('\n');

        var task = Array.FindIndex(lines, l => l.StartsWith("YOUR TASK", StringComparison.Ordinal));
        var verify = Array.FindIndex(lines, l => l.StartsWith("  verify    ", StringComparison.Ordinal));

        Assert.True(task >= 0, brief);
        Assert.True(verify > task, brief);
        Assert.Equal($"  verify    {command}", lines[verify]);
    }
}

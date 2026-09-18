using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkState.Core.Domain;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

[Collection(StoreCollection.Name)]
public sealed class JournalTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Event_seq_is_a_monotonic_cursor()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var first = NewEvent(world, "created");
        context.Events.Add(first);
        await context.SaveChangesAsync(Ct);

        var second = NewEvent(world, "status_changed");
        context.Events.Add(second);
        await context.SaveChangesAsync(Ct);

        Assert.True(second.Seq > first.Seq);
    }

    [Fact]
    public async Task An_event_cannot_be_rewritten()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var entry = NewEvent(world, "reported");
        context.Events.Add(entry);
        await context.SaveChangesAsync(Ct);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var inner = fixture.NewContext();
            var stored = await inner.Events.SingleAsync(e => e.Seq == entry.Seq, Ct);
            stored.Summary = "rewritten after the fact";
            await inner.SaveChangesAsync(Ct);
        });

        Assert.Contains("append-only", error.MessageText);
    }

    [Fact]
    public async Task Inserting_an_event_notifies_the_workstate_channel()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var listener = await fixture.DataSource.OpenConnectionAsync(Ct);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, args) => received.TrySetResult(args.Payload);

        await using (var listen = new NpgsqlCommand("LISTEN workstate", listener))
        {
            await listen.ExecuteNonQueryAsync(Ct);
        }

        long seq;
        await using (var context = fixture.NewContext())
        {
            var entry = NewEvent(world, "verified");
            context.Events.Add(entry);
            await context.SaveChangesAsync(Ct);
            seq = entry.Seq;
        }

        await listener.WaitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Contains($"\"seq\" : {seq}", payload);
        Assert.Contains("\"kind\" : \"verified\"", payload);
    }

    [Fact]
    public async Task A_verification_records_the_result_line_and_who_ran_it()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var task = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "v1", 1m);
        task.VerifyCmd = "dotnet test mcp/WorkState.Tests/WorkState.Tests.csproj";
        context.Items.Add(task);
        await context.SaveChangesAsync(Ct);

        var attempt = new Attempt
        {
            Id = Guid.CreateVersion7(),
            ItemId = task.Id,
            N = 1,
            AgentName = "wsu1-store",
            AgentKind = "general-purpose",
        };
        context.Attempts.Add(attempt);
        await context.SaveChangesAsync(Ct);

        context.Verifications.Add(new Verification
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ItemId = task.Id,
            AttemptId = attempt.Id,
            Command = task.VerifyCmd,
            ResultLine = "Passed! - Failed: 0, Passed: 19",
            Passed = true,
            Counts = """{"passed":19,"failed":0}""",
            RunBy = VerifyRunner.Orchestrator,
        });
        context.Reports.Add(new Report
        {
            Id = Guid.CreateVersion7(),
            AttemptId = attempt.Id,
            Status = "done",
            Deviations = "none",
        });
        await context.SaveChangesAsync(Ct);

        var stored = await context.Verifications.AsNoTracking().SingleAsync(v => v.ItemId == task.Id, Ct);
        Assert.Equal(VerifyRunner.Orchestrator, stored.RunBy);
        Assert.Contains("Passed!", stored.ResultLine);
    }

    [Fact]
    public async Task An_attempt_number_cannot_repeat_for_one_item()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var task = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "v2", 1m);
        await using (var context = fixture.NewContext())
        {
            context.Items.Add(task);
            context.Attempts.Add(new Attempt { Id = Guid.CreateVersion7(), ItemId = task.Id, N = 1 });
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Attempts.Add(new Attempt { Id = Guid.CreateVersion7(), ItemId = task.Id, N = 1 });
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task Two_reports_for_one_attempt_are_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var task = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "v3", 1m);
        var attempt = new Attempt { Id = Guid.CreateVersion7(), ItemId = task.Id, N = 1 };

        await using (var context = fixture.NewContext())
        {
            context.Items.Add(task);
            context.Attempts.Add(attempt);
            context.Reports.Add(new Report { Id = Guid.CreateVersion7(), AttemptId = attempt.Id, Status = "done" });
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Reports.Add(new Report
            {
                Id = Guid.CreateVersion7(),
                AttemptId = attempt.Id,
                Status = "partial",
            });
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("23505", error.SqlState);
    }

    private static Event NewEvent(StoreWorld world, string kind) => new()
    {
        WorkspaceId = world.WorkspaceId,
        ItemId = world.RunA,
        Actor = "wsu1-store",
        Role = ActorRole.Orchestrator,
        Kind = kind,
        Summary = $"{kind} on run-a",
    };
}

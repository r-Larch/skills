using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// ws/10, observed live 2026-10-22: two runs each holding a task <c>2</c>, and <c>work_get</c> on
/// <c>m0/2</c> and <c>ws/2</c> printed the same block twice — both items' criteria and evidence
/// interleaved, the other run's commit, an unqualified URI. The database was right; the read side
/// fetched by id and then keyed the rows by bare ref. Every world here is that shape: <c>1.1</c> in
/// <c>run-a</c> and in <c>run-b</c>, each with its own criteria, evidence and commit.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ReadsByIdTests(PostgresFixture fixture)
{
    private const string ShaA = "aaaa111bbbb2222cccc3333dddd4444eeee5555";
    private const string ShaB = "ffff666677778888999900001111222233334444";

    [Fact]
    public async Task Work_get_on_each_qualified_ref_shows_only_that_items_criteria_evidence_and_commit()
    {
        var world = await TwinsAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.World.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var a = await GetAsync(client, "run-a/1.1");
        var b = await GetAsync(client, "run-b/1.1");

        Assert.Contains("criterion A one", a, StringComparison.Ordinal);
        Assert.Contains("criterion A two", a, StringComparison.Ordinal);
        Assert.Contains("evidence of A", a, StringComparison.Ordinal);
        Assert.Contains(ShaA[..7], a, StringComparison.Ordinal);
        Assert.DoesNotContain("criterion B", a, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence of B", a, StringComparison.Ordinal);
        Assert.DoesNotContain(ShaB[..7], a, StringComparison.Ordinal);

        Assert.Contains("criterion B only", b, StringComparison.Ordinal);
        Assert.Contains("evidence of B", b, StringComparison.Ordinal);
        Assert.Contains(ShaB[..7], b, StringComparison.Ordinal);
        Assert.DoesNotContain("criterion A", b, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence of A", b, StringComparison.Ordinal);
        Assert.DoesNotContain(ShaA[..7], b, StringComparison.Ordinal);

        Assert.Contains(Glyph.Evidence(2, 2), a, StringComparison.Ordinal);
        Assert.Contains(Glyph.Evidence(1, 1), b, StringComparison.Ordinal);
    }

    /// <summary>Both in one call — the exact shape of the live report.</summary>
    [Fact]
    public async Task Work_get_on_both_twins_in_one_call_renders_two_different_blocks()
    {
        var world = await TwinsAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.World.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var text = await ReadServer.CallAsync(
            client,
            "work_get",
            new Dictionary<string, object?> { ["refs"] = new[] { "run-a/1.1", "run-b/1.1" } },
            Ct);

        var lines = text.Split('\n');
        var split = Array.FindIndex(lines, l => l.Contains("work://item/run-a/1.1/brief", StringComparison.Ordinal));
        Assert.True(split > 0, text);

        var first = string.Join("\n", lines[..(split + 1)]);
        var second = string.Join("\n", lines[(split + 1)..]);

        Assert.Single(lines, l => l.Contains("criterion A one", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("criterion B only", StringComparison.Ordinal));
        Assert.DoesNotContain("criterion B", first, StringComparison.Ordinal);
        Assert.DoesNotContain("criterion A", second, StringComparison.Ordinal);
        Assert.Contains("work://item/run-b/1.1", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_tree_over_the_project_shows_each_rows_own_commit_and_criteria_rollup()
    {
        var world = await TwinsAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.World.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var tree = await ReadServer.CallAsync(
            client,
            "work_tree",
            new Dictionary<string, object?> { ["scope"] = world.ProjectRef },
            Ct);

        var rows = tree.Split('\n').Where(l => l.TrimStart().StartsWith("1.1 ", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, rows.Count);

        // Document order: run-a (seq 1) before run-b (seq 9).
        Assert.Contains(ShaA[..7], rows[0], StringComparison.Ordinal);
        Assert.Contains(Glyph.Evidence(2, 2), rows[0], StringComparison.Ordinal);
        Assert.DoesNotContain(ShaB[..7], rows[0], StringComparison.Ordinal);

        Assert.Contains(ShaB[..7], rows[1], StringComparison.Ordinal);
        Assert.Contains(Glyph.Evidence(1, 1), rows[1], StringComparison.Ordinal);
        Assert.DoesNotContain(ShaA[..7], rows[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// doc 01 §8: a tool prints the exact URI. When the bare ref names two items, the bare URI is a
    /// refusal waiting to happen, so the printed one is qualified; a unique ref stays bare.
    /// </summary>
    [Fact]
    public async Task Item_uris_are_qualified_only_when_the_ref_is_not_unique()
    {
        var world = await TwinsAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.World.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var twin = await GetAsync(client, "run-b/1.1");
        var unique = await GetAsync(client, "9.1");

        Assert.Contains("  uri       work://item/run-b/1.1\n", twin, StringComparison.Ordinal);
        Assert.Contains("  brief     work://item/run-b/1.1/brief", twin, StringComparison.Ordinal);
        Assert.Contains("  uri       work://item/9.1\n", unique, StringComparison.Ordinal);
        Assert.Contains("  brief     work://item/9.1/brief", unique, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_qualified_brief_resource_resolves_to_the_right_item_and_prints_its_own_uri()
    {
        var world = await TwinsAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.World.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var b = await ResourceAsync(client, "work://item/run-b/1.1/brief");
        var a = await ResourceAsync(client, "work://item/run-a/1.1/brief");
        var item = await ResourceAsync(client, "work://item/run-b/1.1");

        Assert.Contains("YOUR TASK  1.1 — Twin in run B", b, StringComparison.Ordinal);
        Assert.Contains("criterion B only", b, StringComparison.Ordinal);
        Assert.DoesNotContain("criterion A", b, StringComparison.Ordinal);
        Assert.Contains("  brief     work://item/run-b/1.1/brief", b, StringComparison.Ordinal);

        Assert.Contains("YOUR TASK  1.1 — Twin in run A", a, StringComparison.Ordinal);
        Assert.Contains("criterion A one", a, StringComparison.Ordinal);
        Assert.DoesNotContain("criterion B", a, StringComparison.Ordinal);
        Assert.Contains("  brief     work://item/run-a/1.1/brief", a, StringComparison.Ordinal);

        Assert.Contains("criterion B only", item, StringComparison.Ordinal);
        Assert.DoesNotContain("criterion A", item, StringComparison.Ordinal);
    }

    /// <summary>
    /// The write guard's pin: closing <c>run-a/1.1</c> must not be satisfied by <c>run-b/1.1</c>'s
    /// orchestrator verification and checked criterion. Its permissive twin is the same close once
    /// run-a's own evidence exists.
    /// </summary>
    [Fact]
    public async Task Closing_one_twin_is_refused_on_the_other_twins_verification_and_criteria()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        var runB = await world.ItemAsync("run-b");
        var phaseB = await WriteSeed.ChildAsync(world, runB.Id, "1", kind: "phase");
        var twinB = await WriteSeed.ChildAsync(world, phaseB, "1.1");

        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "run-a's own criterion", ticked: false);

        await WriteSeed.CriterionAsync(world, twinB, 1, "run-b's criterion", ticked: true);
        await WriteSeed.VerificationAsync(world, twinB, VerifyRunner.Orchestrator);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "run-a/1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains("criterion c1", refused.Text, StringComparison.Ordinal);
        Assert.Contains("run_by=orchestrator", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, (await Seed.ReloadAsync(fixture, world.TaskId)).Status);

        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        await world.Writer.UpdateAsync(
            world.WorkspaceId,
            WriteSeed.Orchestrator,
            "run-a/1.1",
            new UpdateArgs(Criteria: [new CriterionArg("c1", Checked: true, Evidence: "the assertion")]),
            Ct);

        var allowed = await world.Writer.StatusAsync(
            world.WorkspaceId, "run-a/1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.False(allowed.Refused, allowed.Text);
        Assert.Equal(ItemStatus.Done, (await Seed.ReloadAsync(fixture, world.TaskId)).Status);
        Assert.NotEqual(ItemStatus.Done, (await Seed.ReloadAsync(fixture, twinB)).Status);
    }

    private static Task<string> GetAsync(ModelContextProtocol.Client.McpClient client, string reference)
        => ReadServer.CallAsync(
            client,
            "work_get",
            new Dictionary<string, object?> { ["refs"] = new[] { reference } },
            Ct);

    private static async Task<string> ResourceAsync(ModelContextProtocol.Client.McpClient client, string uri)
    {
        var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
        return string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }

    private sealed record Twins(WriteWorld World, string ProjectRef);

    /// <summary>
    /// <c>run-a/1.1</c>: two checked criteria, its own verification and commit. <c>run-b/1.1</c>,
    /// under a phase <c>1</c> of its own: one checked criterion, a different verification and commit.
    /// Both done, so the tree prints each commit.
    /// </summary>
    private async Task<Twins> TwinsAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);

        await using var db = fixture.NewContext();
        var runB = await db.Items.FirstAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "run-b", Ct);
        var project = await db.Items.FirstAsync(i => i.Id == world.ProjectId, Ct);

        var phaseB = Seed.NewItem(world.WorkspaceId, runB.Id, "phase", "1", 2m, "Phase one, run B");
        db.Items.Add(phaseB);
        await db.SaveChangesAsync(Ct);

        var twinB = Seed.NewItem(world.WorkspaceId, phaseB.Id, "task", "1.1", 1m, "Twin in run B");
        db.Items.Add(twinB);

        var twinA = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);
        twinA.Title = "Twin in run A";
        await db.SaveChangesAsync(Ct);

        await WriteSeed.CriterionAsync(world, twinA.Id, 1, "criterion A one", ticked: true);
        await WriteSeed.CriterionAsync(world, twinA.Id, 2, "criterion A two", ticked: true);
        await WriteSeed.CriterionAsync(world, twinB.Id, 1, "criterion B only", ticked: true);

        db.Verifications.AddRange(
            Verified(world, twinA.Id, "evidence of A: Passed 12"),
            Verified(world, twinB.Id, "evidence of B: Passed 34"));

        db.Artifacts.AddRange(
            new Artifact { Id = Guid.CreateVersion7(), ItemId = twinA.Id, Kind = "commit", Value = ShaA, At = DateTimeOffset.UtcNow },
            new Artifact { Id = Guid.CreateVersion7(), ItemId = twinB.Id, Kind = "commit", Value = ShaB, At = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync(Ct);

        await WriteSeed.StatusAsync(world, twinA.Id, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, twinB.Id, ItemStatus.Done);

        return new Twins(world, project.Ref);
    }

    private static Verification Verified(WriteWorld world, Guid itemId, string line) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = world.WorkspaceId,
        ItemId = itemId,
        Command = "dotnet test --filter Guard",
        ResultLine = line,
        Passed = true,
        RunBy = VerifyRunner.Orchestrator,
    };
}

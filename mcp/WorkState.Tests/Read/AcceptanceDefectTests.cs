using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// The five defects the cold-start acceptance found (<c>mcp/ACCEPTANCE.md</c> §6,
/// <c>FINDINGS-live.md</c>). Every one of them <b>passed its own unit tests</b> on the day it was
/// found — they were caught by a transcript, not by a suite — so each gets an assertion here, shaped
/// the way the transcript found it rather than the way the code is organised.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AcceptanceDefectTests(PostgresFixture fixture)
{
    /// <summary>
    /// <b>A-2, the worst of them.</b> <c>work://item/3.1/brief</c> answered
    /// <c>"this server holds no work yet"</c> for a ref that existed in two runs — not an error, a
    /// confident wrong answer, and an agent that believed it would report the store empty and stop.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_item_uri_refuses_and_never_claims_the_server_is_empty()
    {
        var world = await AmbiguousWorldAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var body = await ResourceAsync(client, "work://item/1.1/brief");

        Assert.DoesNotContain("holds no work yet", body, StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", body, StringComparison.Ordinal);
        Assert.Contains("run-a/1.1", body, StringComparison.Ordinal);
        Assert.Contains("run-b/1.1", body, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(body);
    }

    /// <summary>
    /// <b>A-4.</b> The agent composed <c>work://item/entity-store/3.1/brief</c> — qualified and
    /// sensible — and got a 404, then composed the bare one and got A-2. Both forms now read back,
    /// and the qualified URI is the one a tool prints.
    /// </summary>
    [Fact]
    public async Task A_qualified_item_uri_reads_back_in_both_its_projections()
    {
        var world = await AmbiguousWorldAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        Assert.Equal("work://item/run-b/1.1", WorkUris.Item("run-b/1.1"));

        var item = await ResourceAsync(client, "work://item/run-b/1.1");
        var brief = await ResourceAsync(client, "work://item/run-b/1.1/brief");

        Assert.Contains("1.1", item, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUSED", item, StringComparison.Ordinal);
        Assert.Contains("YOUR TASK  1.1", brief, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A-1.</b> The agent was in <c>entity-store</c> and asked for <c>3.1</c>; the refusal named
    /// <c>document-store/3.1</c>. It is callable, it satisfies doc 04 §3 to the letter, and it points
    /// at another run's item — which is worse than naming no candidate, because a careful agent acts
    /// on a callable line.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_ref_lists_every_candidate_and_puts_the_callers_own_run_first()
    {
        var world = await AmbiguousWorldAsync();
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        // "the caller's own run" is the scope an unqualified call lands on, so the test says which
        // one that is rather than assuming it.
        var home = await ReadServer.CallAsync(client, "work_brief", null, Ct);
        Assert.StartsWith("RUN run-a", home, StringComparison.Ordinal);

        var text = await ReadServer.CallAsync(
            client,
            "work_get",
            new Dictionary<string, object?> { ["refs"] = new[] { "1.1" } },
            Ct);

        var line = text.Split('\n').Single(l => l.Contains("exists in 2 runs", StringComparison.Ordinal));

        Assert.Contains("run-a/1.1", line, StringComparison.Ordinal);
        Assert.Contains("run-b/1.1", line, StringComparison.Ordinal);
        Assert.True(
            line.IndexOf("run-a/1.1", StringComparison.Ordinal)
            < line.IndexOf("run-b/1.1", StringComparison.Ordinal),
            $"the caller's own run is not named first: {line}");
    }

    /// <summary>
    /// <b>A-3.</b> <c>work_brief(scope:"entity-store/3.1")</c> rendered <c>RUN 3.1 · project
    /// phase-3</c>: a task labelled a run, its phase labelled the project. This is the tutorial
    /// surface — it is where an agent learns the tree — so mislabelling its own tree is not cosmetic.
    /// </summary>
    [Fact]
    public async Task A_brief_on_a_task_labels_it_a_task_and_names_the_real_run_and_project()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        var task = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "1.1" },
            Ct);

        var head = task.Split('\n')[0];

        Assert.StartsWith("TASK 1.1", head, StringComparison.Ordinal);
        Assert.Contains("·  run run-a", head, StringComparison.Ordinal);
        Assert.DoesNotContain("project 1", head, StringComparison.Ordinal);

        var phase = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "2" },
            Ct);

        Assert.StartsWith("PHASE 2", phase.Split('\n')[0], StringComparison.Ordinal);

        var run = await ReadServer.CallAsync(
            client,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "run-a" },
            Ct);

        Assert.StartsWith("RUN run-a", run.Split('\n')[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>F-L6.</b> The only thing that created the schema was the corpus importer, so standing up
    /// an empty server meant inventing a directory of markdown to import nothing from. doc 06 §4
    /// promises the empty-server path explains itself through <c>work_brief</c>, and this is that
    /// promise kept without an ingest tool: a database with no tables in it, and one call.
    /// </summary>
    [Fact]
    public async Task The_api_creates_its_own_schema_so_a_first_run_needs_no_importer()
    {
        var name = $"fresh_{Guid.CreateVersion7():n}";
        var connectionString = await BlankDatabaseAsync(name);

        Assert.Equal(0, await TableCountAsync(connectionString));

        await using var server = await ReadServer.StartAsync(connectionString, null, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var text = await ReadServer.CallAsync(client, "work_brief", null, Ct);

        Assert.Contains("NEXT   work_create(", text, StringComparison.Ordinal);
        Assert.True(await TableCountAsync(connectionString) > 0, "the API left the database empty");
    }

    private static async Task<string> ResourceAsync(
        ModelContextProtocol.Client.McpClient client,
        string uri)
    {
        var read = await client.ReadResourceAsync(uri, cancellationToken: Ct);
        return string.Join("\n", read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }

    /// <summary>A workspace where <c>1.1</c> is a real ref in two different runs.</summary>
    private async Task<WriteWorld> AmbiguousWorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);

        await using var db = fixture.NewContext();
        var runB = await db.Items.FirstAsync(
            i => i.WorkspaceId == world.WorkspaceId && i.Ref == "run-b", Ct);

        await WriteSeed.ChildAsync(world, runB.Id, "1.1", status: ItemStatus.Open);
        return world;
    }

    /// <summary>
    /// A database on the test container with nothing in it at all — not even the migrations table.
    /// </summary>
    private async Task<string> BlankDatabaseAsync(string name)
    {
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }
            .ConnectionString;
    }

    private static async Task<int> TableCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        await using var count = new NpgsqlCommand(
            "SELECT count(*)::int FROM information_schema.tables WHERE table_schema = 'public';",
            connection);

        return (int)(await count.ExecuteScalarAsync(Ct))!;
    }
}

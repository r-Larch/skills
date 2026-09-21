using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using WorkState.Api.App;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Mcp.Tools;
using WorkState.Tests.Import;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// "One query layer, two presentations" (doc 09 §8), made into a test rather than an intention. If
/// the human and the agent are ever shown different bytes for the same scope, the dashboard is
/// lying about what the agent sees.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AppBriefTests(PostgresFixture fixture)
{
    private const string ParkedAgent = "worker-parked";

    /// <summary>
    /// <c>work_brief</c> is not a pure read — it sweeps lapsed leases before rendering and consumes
    /// addressed notes after a worker brief, and both writes change the bytes the <em>next</em> call
    /// produces. So a naive "call both, compare" passes or fails on call order, and the most
    /// valuable test in this unit would have passed for the wrong reason.
    /// <para>
    /// Both orders are therefore run against <b>one</b> world, restored to its seeded state in
    /// between, and the two surfaces are compared position by position: what the API returns going
    /// first against what the tool returns going first, and likewise for the call that follows.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("resume")]
    [InlineData("worker")]
    public async Task The_brief_endpoint_is_byte_identical_to_work_brief_in_either_call_order(string variant)
    {
        var run = await ImportAsync();
        var lease = await ExpireALeaseAsync(run);
        await AddressANoteAsync(run);

        await using var server = await StartAsync(run.WorkspaceSlug);
        var client = await server.SignInAsync(Ct);
        await using var mcp = await server.ConnectAsync(ReadServer.ControllerToken, Ct);

        async Task<string> ApiAsync()
        {
            var response = await client.GetAsync($"/api/runs/sample-run/brief?for={variant}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        Task<string> ToolAsync() => ReadServer.CallAsync(
            mcp,
            "work_brief",
            new Dictionary<string, object?> { ["scope"] = "sample-run", ["for"] = variant },
            Ct);

        var apiLeading = await ApiAsync();
        var afterApiLed = await MutationsAsync(run);
        var toolFollowing = await ToolAsync();

        await RestoreAsync(run, lease);

        var toolLeading = await ToolAsync();
        var afterToolLed = await MutationsAsync(run);
        var apiFollowing = await ApiAsync();

        Assert.Equal(apiLeading, toolLeading);
        Assert.Equal(toolFollowing, apiFollowing);

        // And whichever surface went first did the housekeeping, so the equality above is a claim
        // about two surfaces rather than about a fixture with nothing to disturb: the lapsed lease
        // is released by both, and the addressed note is consumed by both — but only on the worker
        // variant, which is the one call that is supposed to consume it.
        foreach (var leader in (AfterFirst[])[afterApiLed, afterToolLed])
        {
            Assert.Equal("expired", leader.LeaseReleaseReason);
            Assert.Equal(variant == "worker" ? 0 : 1, leader.UnconsumedNotes);
        }
    }

    /// <summary>
    /// The equality above would still hold the day someone copied the sweep into the endpoint — and
    /// would quietly stop holding the day the copy drifted. So: both surfaces must reach the shared
    /// <see cref="BoardReader.BriefSessionAsync"/>, and neither may perform the writes itself.
    /// </summary>
    [Fact]
    public void Neither_brief_surface_owns_a_copy_of_the_sweep_they_both_depend_on()
    {
        var shared = Method(typeof(BoardReader), nameof(BoardReader.BriefSessionAsync));
        var sweep = Method(typeof(Housekeeping), nameof(Housekeeping.SweepAsync));
        var consume = Method(typeof(Housekeeping), nameof(Housekeeping.ConsumeNotesAsync));

        var tool = Method(typeof(WorkReadTools), nameof(WorkReadTools.BriefAsync));
        var endpoint = Method(typeof(AppEndpoints), "BriefAsync");

        Assert.True(Calls(tool, shared), "work_brief must render through BoardReader.BriefSessionAsync");
        Assert.True(Calls(endpoint, shared), "GET /api/runs/{ref}/brief must render through BoardReader.BriefSessionAsync");

        Assert.False(Calls(tool, sweep), "work_brief must not sweep itself");
        Assert.False(Calls(tool, consume), "work_brief must not consume notes itself");
        Assert.False(Calls(endpoint, sweep), "the brief endpoint must not sweep itself");
        Assert.False(Calls(endpoint, consume), "the brief endpoint must not consume notes itself");
    }

    [Fact]
    public async Task The_brief_endpoint_serves_text_plain_rather_than_a_string_inside_json()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        var client = await server.SignInAsync(Ct);

        var response = await client.GetAsync("/api/runs/sample-run/brief", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("RUN sample-run", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_ref_is_a_404_rather_than_an_empty_brief()
    {
        var run = await ImportAsync();
        await using var server = await StartAsync(run.WorkspaceSlug);
        var client = await server.SignInAsync(Ct);

        var response = await client.GetAsync("/api/runs/no-such-run/brief", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>What the store looked like the moment the first of the two calls returned.</summary>
    private sealed record AfterFirst(string? LeaseReleaseReason, int UnconsumedNotes);

    private static async Task<AfterFirst> MutationsAsync(ImportedRun run)
    {
        await using var db = run.NewContext();

        var lease = await db.Claims.FirstAsync(c => c.AgentName == ParkedAgent, Ct);

        var unconsumed = await db.Notes.CountAsync(
            n => n.ToItemId == run.RunItemId && n.ConsumedAt == null, Ct);

        return new AfterFirst(lease.ReleaseReason, unconsumed);
    }

    /// <summary>A lapsed lease on an <c>in_progress</c> item — what the sweep exists to notice.</summary>
    private static async Task<Claim> ExpireALeaseAsync(ImportedRun run)
    {
        var item = await run.RequireItemAsync("T1.1");

        await using var db = run.NewContext();

        var tracked = await db.Items.FindAsync([item.Id], Ct);
        tracked!.Status = ItemStatus.InProgress;
        tracked.StartedAt = DateTimeOffset.UtcNow.AddHours(-2);

        var lease = new Claim
        {
            Id = Guid.CreateVersion7(),
            ItemId = item.Id,
            SerializationKey = $"brief-{item.Id:n}",
            AgentName = ParkedAgent,
            Role = ActorRole.Worker,
            AcquiredAt = DateTimeOffset.UtcNow.AddHours(-2),
            HeartbeatAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-31),
        };

        db.Claims.Add(lease);
        await db.SaveChangesAsync(Ct);

        return lease;
    }

    /// <summary>
    /// Puts the seeded state back so the second order starts where the first did. The instants are
    /// restored to the values they were seeded with rather than recomputed: the brief renders ages
    /// rounded down, and a lease re-expired a second later would read the same only by luck.
    /// </summary>
    private static async Task RestoreAsync(ImportedRun run, Claim lease)
    {
        await using var db = run.NewContext();

        var claim = await db.Claims.FirstAsync(c => c.Id == lease.Id, Ct);
        claim.ReleasedAt = null;
        claim.ReleaseReason = null;
        claim.ExpiresAt = lease.ExpiresAt;

        var item = await db.Items.FirstAsync(i => i.Id == lease.ItemId, Ct);
        item.Status = ItemStatus.InProgress;

        foreach (var note in await db.Notes.Where(n => n.ToItemId == run.RunItemId).ToListAsync(Ct))
        {
            note.ConsumedAt = null;
            note.ConsumedBy = null;
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>An unconsumed note addressed to the run — what a worker brief consumes as it renders.</summary>
    private static async Task AddressANoteAsync(ImportedRun run)
    {
        var writer = new WorkWriter(
            run.Fixture.DataSource,
            new BoardQueries(run.Fixture.DataSource),
            new Housekeeping(run.Fixture.DataSource));

        var result = await writer.NoteAsync(
            run.WorkspaceId,
            new Actor("orchestrator-a", ActorRole.Orchestrator),
            "carry_forward",
            "The audience separation lands before anything narrows a scheme.",
            "sample-run",
            null,
            Ct);

        Assert.False(result.Refused, result.Text);
    }

    private static MethodInfo Method(Type type, string name)
        => type.GetMethod(
               name,
               BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
           ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{name} was not found");

    /// <summary>
    /// Whether <paramref name="caller"/>'s compiled body contains a call to <paramref name="target"/>.
    /// An async method's body is in the state machine the compiler generated for it, so that is what
    /// is read; the scan is anchored on the two call opcodes and every candidate token is resolved,
    /// so an operand that merely looks like an opcode cannot name this exact method by accident.
    /// </summary>
    private static bool Calls(MethodInfo caller, MethodInfo target)
    {
        var body = Compiled(caller);
        var il = body.GetMethodBody()?.GetILAsByteArray()
                 ?? throw new Xunit.Sdk.XunitException($"{caller.Name} has no readable body");

        var module = body.Module;
        var typeArguments = body.DeclaringType?.GetGenericArguments();

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
            {
                continue;
            }

            MethodBase? resolved;
            try
            {
                resolved = module.ResolveMethod(BitConverter.ToInt32(il, i + 1), typeArguments, null);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (resolved is not null
                && resolved.MetadataToken == target.MetadataToken
                && resolved.Module == target.Module)
            {
                return true;
            }
        }

        return false;
    }

    private static MethodInfo Compiled(MethodInfo method)
    {
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is not { } async)
        {
            return method;
        }

        return async.StateMachineType.GetMethod(
                   "MoveNext",
                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
               ?? throw new Xunit.Sdk.XunitException($"{method.Name}'s state machine has no MoveNext");
    }

    private Task<ImportedRun> ImportAsync()
        => Corpus.ImportAsync(fixture, "sample-run", $"brief-{Guid.CreateVersion7():n}");

    private Task<AppServer> StartAsync(string slug)
        => AppServer.StartAsync(fixture.ConnectionString, slug, Ct);
}

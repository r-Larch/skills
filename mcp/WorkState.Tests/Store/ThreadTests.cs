using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

[Collection(StoreCollection.Name)]
public sealed class ThreadTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Two_live_write_claims_on_one_tree_are_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var key = $"P:/worktrees/{Guid.CreateVersion7():n}";

        var first = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "cl1", 1m);
        var second = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "cl2", 2m);

        await using (var context = fixture.NewContext())
        {
            context.Items.AddRange(first, second);
            context.Claims.Add(NewClaim(first.Id, key, writes: true, "agent-one"));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Claims.Add(NewClaim(second.Id, key, writes: true, "agent-two"));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("claim_one_writer_per_tree", error.ConstraintName);
    }

    [Fact]
    public async Task Write_claims_on_different_trees_both_succeed()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var inRunA = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "par1", 1m);
        var inRunB = Seed.NewItem(world.WorkspaceId, world.RunB, "task", "par2", 1m);
        context.Items.AddRange(inRunA, inRunB);
        await context.SaveChangesAsync(Ct);

        context.Claims.AddRange(
            NewClaim(inRunA.Id, $"P:/worktrees/a-{Guid.CreateVersion7():n}", writes: true, "orchestrator-a"),
            NewClaim(inRunB.Id, $"P:/worktrees/b-{Guid.CreateVersion7():n}", writes: true, "orchestrator-b"));

        await context.SaveChangesAsync(Ct);

        Assert.Equal(2, await context.Claims
            .CountAsync(c => c.ItemId == inRunA.Id || c.ItemId == inRunB.Id, Ct));
    }

    [Fact]
    public async Task A_read_only_claim_does_not_take_the_tree_lock()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var key = $"P:/worktrees/{Guid.CreateVersion7():n}";

        await using var context = fixture.NewContext();
        var writer = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "ro1", 1m);
        var reader = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "ro2", 2m);
        context.Items.AddRange(writer, reader);
        await context.SaveChangesAsync(Ct);

        context.Claims.AddRange(
            NewClaim(writer.Id, key, writes: true, "worker"),
            NewClaim(reader.Id, key, writes: false, "explore"));

        await context.SaveChangesAsync(Ct);

        Assert.Equal(2, await context.Claims.CountAsync(c => c.SerializationKey == key, Ct));
    }

    [Fact]
    public async Task Releasing_a_claim_frees_the_tree_for_the_next_writer()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var key = $"P:/worktrees/{Guid.CreateVersion7():n}";

        await using var context = fixture.NewContext();
        var first = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "rel1", 1m);
        var second = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "rel2", 2m);
        context.Items.AddRange(first, second);
        await context.SaveChangesAsync(Ct);

        var held = NewClaim(first.Id, key, writes: true, "worker-one");
        context.Claims.Add(held);
        await context.SaveChangesAsync(Ct);

        held.ReleasedAt = DateTimeOffset.UtcNow;
        held.ReleaseReason = "done";
        context.Claims.Add(NewClaim(second.Id, key, writes: true, "worker-two"));

        await context.SaveChangesAsync(Ct);

        Assert.Single(await context.Claims
            .Where(c => c.SerializationKey == key && c.ReleasedAt == null)
            .ToListAsync(Ct));
    }

    [Fact]
    public async Task One_item_cannot_hold_two_live_claims()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var item = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "live1", 1m);
        await using (var context = fixture.NewContext())
        {
            context.Items.Add(item);
            context.Claims.Add(NewClaim(item.Id, $"tree-{Guid.CreateVersion7():n}", writes: false, "a"));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Claims.Add(NewClaim(item.Id, $"tree-{Guid.CreateVersion7():n}", writes: false, "b"));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("claim_one_live_per_item", error.ConstraintName);
    }

    [Fact]
    public async Task The_same_assumption_ref_under_two_runs_is_accepted()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var inRunA = NewAssumption(world.WorkspaceId, world.RunA, "A1");
        var inRunB = NewAssumption(world.WorkspaceId, world.RunB, "A1");
        context.Assumptions.AddRange(inRunA, inRunB);

        await context.SaveChangesAsync(Ct);

        Assert.Equal(world.RunA, inRunA.RefScopeId);
        Assert.Equal(world.RunB, inRunB.RefScopeId);
    }

    [Fact]
    public async Task The_same_assumption_ref_twice_in_one_run_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using (var context = fixture.NewContext())
        {
            context.Assumptions.Add(NewAssumption(world.WorkspaceId, world.RunA, "A6"));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.Assumptions.Add(NewAssumption(world.WorkspaceId, world.RunA, "A6"));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("assumption_ref_unique", error.ConstraintName);
    }

    [Fact]
    public async Task An_assumption_resolved_without_a_timestamp_is_refused()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            var assumption = NewAssumption(world.WorkspaceId, world.RunA, "A9");
            assumption.State = AssumptionState.Confirmed;
            context.Assumptions.Add(assumption);
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("assumption_resolved", error.ConstraintName);
    }

    /// <summary>
    /// doc 02 §7, corrected by import finding #2: the corpus writes `—` in the reversal column of every
    /// assumption it has already resolved, and forcing a value there made the importer invent `medium`.
    /// </summary>
    [Fact]
    public async Task A_resolved_assumption_stores_no_reversal_cost_and_an_open_one_still_does()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var resolved = NewAssumption(world.WorkspaceId, world.RunA, "A-none");
        resolved.Reversal = null;
        resolved.State = AssumptionState.Confirmed;
        resolved.ResolvedAt = DateTimeOffset.UtcNow;
        resolved.ResolutionNote = "settled by the 2.1 worker; the cost stopped mattering";

        var open = NewAssumption(world.WorkspaceId, world.RunA, "A-cost");
        open.Reversal = ReversalCost.Irreversible;

        context.Assumptions.AddRange(resolved, open);
        await context.SaveChangesAsync(Ct);

        var stored = await context.Assumptions.AsNoTracking()
            .Where(a => a.RefScopeId == world.RunA)
            .ToDictionaryAsync(a => a.Ref, Ct);

        Assert.Null(stored["A-none"].Reversal);
        Assert.Equal(ReversalCost.Irreversible, stored["A-cost"].Reversal);
    }

    /// <summary>
    /// doc 02 §7, import finding #3: the enum keeps the level, and the provenance beside it — which is
    /// the part a later reader needs — has nowhere else to go.
    /// </summary>
    [Fact]
    public async Task An_evidence_note_round_trips_across_lines_and_outside_ascii()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        const string note = "confirmed: the key is in configuration\nMEDIUM — matches the real trial flow · ✅";

        await using var context = fixture.NewContext();
        var assumption = NewAssumption(world.WorkspaceId, world.RunA, "A-note");
        assumption.EvidenceNote = note;
        context.Assumptions.Add(assumption);
        await context.SaveChangesAsync(Ct);

        var stored = await context.Assumptions.AsNoTracking().SingleAsync(a => a.Id == assumption.Id, Ct);

        Assert.Equal(note, stored.EvidenceNote);
    }

    [Fact]
    public async Task A_note_addressed_to_an_item_stays_pending_until_it_is_consumed()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        var target = Seed.NewItem(world.WorkspaceId, world.RunA, "task", "n1", 1m);
        context.Items.Add(target);
        await context.SaveChangesAsync(Ct);

        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            FromItemId = world.RunA,
            ToItemId = target.Id,
            Kind = NoteKind.DoNotRederive,
            Body = "psql is not on PATH; reach postgres through docker exec.",
            ToSelector = """{"under":"run-a","kind":"task"}""",
        };
        context.Notes.Add(note);
        await context.SaveChangesAsync(Ct);

        Assert.Single(await context.Notes
            .Where(n => n.ToItemId == target.Id && n.ConsumedAt == null)
            .ToListAsync(Ct));

        var correction = new Note
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ToItemId = target.Id,
            Kind = NoteKind.Correction,
            Body = "superseded: psql is available in the container image.",
            CorrectsNoteId = note.Id,
        };
        context.Notes.Add(correction);
        note.ConsumedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(Ct);

        var stored = await context.Notes.AsNoTracking().SingleAsync(n => n.Id == correction.Id, Ct);
        Assert.Equal(note.Id, stored.CorrectsNoteId);
    }

    [Fact]
    public async Task A_constraint_rule_is_queryable_by_the_risk_it_covers()
    {
        var world = await Seed.CreateWorldAsync(fixture);

        await using var context = fixture.NewContext();
        context.ConstraintRules.Add(new ConstraintRule
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ScopeItemId = world.RunA,
            Kind = ConstraintKind.RedLine,
            Text = "Never write an unscoped DELETE or DROP.",
            Route = "Scope by id, always.",
            CoversRisk = ["delete", "schema"],
        });
        await context.SaveChangesAsync(Ct);

        var covering = await context.ConstraintRules.AsNoTracking()
            .Where(c => c.WorkspaceId == world.WorkspaceId && c.Active && c.CoversRisk.Contains("schema"))
            .ToListAsync(Ct);

        var rule = Assert.Single(covering);
        Assert.NotNull(rule.Route);
    }

    [Fact]
    public async Task A_token_hash_cannot_be_registered_twice()
    {
        var world = await Seed.CreateWorldAsync(fixture);
        var hash = Guid.CreateVersion7().ToByteArray();

        await using (var context = fixture.NewContext())
        {
            context.AgentTokens.Add(NewToken(world.WorkspaceId, world.RunA, hash, "orchestrator-a"));
            await context.SaveChangesAsync(Ct);
        }

        var error = await Seed.RefusedAsync(async () =>
        {
            await using var context = fixture.NewContext();
            context.AgentTokens.Add(NewToken(world.WorkspaceId, world.RunB, hash, "orchestrator-b"));
            await context.SaveChangesAsync(Ct);
        });

        Assert.Equal("agent_token_hash_idx", error.ConstraintName);
    }

    private static Claim NewClaim(Guid itemId, string serializationKey, bool writes, string agentName) => new()
    {
        Id = Guid.CreateVersion7(),
        ItemId = itemId,
        SerializationKey = serializationKey,
        Writes = writes,
        AgentName = agentName,
        Role = ActorRole.Worker,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
    };

    private static Assumption NewAssumption(Guid workspaceId, Guid scopeItemId, string reference) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = workspaceId,
        ScopeItemId = scopeItemId,
        Ref = reference,
        Statement = "Docker is usable from a background agent here.",
        Evidence = EvidenceLevel.Probed,
        Reversal = ReversalCost.Low,
    };

    private static AgentToken NewToken(Guid workspaceId, Guid scopeItemId, byte[] hash, string agentName) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = workspaceId,
        ScopeItemId = scopeItemId,
        Role = ActorRole.Orchestrator,
        AgentName = agentName,
        TokenHash = hash,
    };
}

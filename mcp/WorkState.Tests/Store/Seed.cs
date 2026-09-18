using Npgsql;
using WorkState.Core.Domain;
using Xunit;

namespace WorkState.Tests.Store;

/// <summary>A workspace with a project and two parallel runs — the shape every test starts from.</summary>
public sealed record StoreWorld(Guid WorkspaceId, Guid ProjectId, Guid RunA, Guid RunB);

public static class Tokens
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}

public static class Seed
{
    public static Item NewItem(
        Guid workspaceId,
        Guid? parentId,
        string kind,
        string reference,
        decimal seq,
        string? title = null)
        => new()
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            ParentId = parentId,
            Kind = kind,
            Ref = reference,
            Seq = seq,
            Title = title ?? reference,
        };

    public static async Task<StoreWorld> CreateWorldAsync(PostgresFixture fixture)
    {
        await using var context = fixture.NewContext();

        var workspace = new Workspace
        {
            Id = Guid.CreateVersion7(),
            Slug = $"ws-{Guid.CreateVersion7():n}",
        };
        context.Workspaces.Add(workspace);

        var project = NewItem(workspace.Id, null, "project", $"p-{Guid.CreateVersion7():n}"[..12], 1m);
        var runA = NewItem(workspace.Id, project.Id, "run", "run-a", 1m);
        var runB = NewItem(workspace.Id, project.Id, "run", "run-b", 2m);
        context.Items.AddRange(project, runA, runB);

        await context.SaveChangesAsync(Tokens.Ct);

        return new StoreWorld(workspace.Id, project.Id, runA.Id, runB.Id);
    }

    /// <summary>
    /// Runs an operation the schema is supposed to refuse and returns the Postgres error, so a test
    /// can assert on which constraint fired rather than merely that something went wrong.
    /// </summary>
    public static async Task<PostgresException> RefusedAsync(Func<Task> operation)
    {
        var error = await Record.ExceptionAsync(operation);
        Assert.NotNull(error);

        for (var candidate = error; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"expected a Postgres constraint violation, got {error.GetType().Name}: {error.Message}");
    }

    public static async Task<Item> ReloadAsync(PostgresFixture fixture, Guid id)
    {
        await using var context = fixture.NewContext();
        return await context.Items.FindAsync([id], Tokens.Ct)
            ?? throw new Xunit.Sdk.XunitException($"item {id} was not found");
    }
}

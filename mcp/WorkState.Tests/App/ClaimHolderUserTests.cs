using Dapper;
using ModelContextProtocol.Client;
using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m62. Users share role names — every OAuth controller is <c>controller</c> — so a claim with no session is the
/// caller's own only when the user matches as well as the name. A claim with no user stays a static token's, as before.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClaimHolderUserTests(PostgresFixture fixture)
{
    private static readonly string Task = $"{ProjectWorld.P1}/run-a/1.1";

    [Fact]
    public async Task ClaimHolder_another_user_with_the_same_agent_name_is_refused_the_retake_and_not_told_it_is_theirs()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await using var a = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);
        await using var b = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);

        await OkAsync(a, "work_status", Start());

        var retake = await ReadServer.CallEitherAsync(b, "work_status", Start(), Ct);

        Assert.True(retake.Refused, retake.Text);
        Assert.Contains("already claimed", retake.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("that is you", retake.Text, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(retake.Text);
        Assert.Equal([$"controller {world.User1Id}"], await LiveClaimsAsync(world));
    }

    [Fact]
    public async Task ClaimHolder_the_user_who_took_the_claim_still_retakes_it()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await using var a = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);

        await OkAsync(a, "work_status", Start());
        var again = await OkAsync(a, "work_status", Start());

        Assert.Contains("extended", again, StringComparison.Ordinal);
        Assert.Equal([$"controller {world.User1Id}"], await LiveClaimsAsync(world));
    }

    [Fact]
    public async Task ClaimHolder_a_static_tokens_claim_has_no_user_and_the_same_static_name_retakes_it()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await using var first = await world.McpAsync(world.Static.Mcp!);
        await using var second = await world.McpAsync(world.Static.Mcp!);

        await OkAsync(first, "work_status", Start());
        var again = await OkAsync(second, "work_status", Start());

        Assert.Contains("extended", again, StringComparison.Ordinal);
        Assert.Equal(["controller "], await LiveClaimsAsync(world));

        // A user with the same name does not inherit a claim that has no user.
        await using var user = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);
        var taken = await ReadServer.CallEitherAsync(user, "work_status", Start(), Ct);
        Assert.True(taken.Refused, taken.Text);
        Assert.DoesNotContain("that is you", taken.Text, StringComparison.Ordinal);
    }

    private static async Task<List<string>> LiveClaimsAsync(ProjectWorld world)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<string>(
            """
            SELECT c.agent_name || ' ' || coalesce(c.holder_user_id::text, '')
            FROM   claim c WHERE c.item_id = @task AND c.released_at IS NULL;
            """,
            new { task = world.One.TaskId })).AsList();
    }

    private static IReadOnlyDictionary<string, object?> Start()
        => new Dictionary<string, object?> { ["ref"] = Task, ["to"] = "in_progress" };

    private static async Task<string> OkAsync(McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
    {
        var (text, refused) = await ReadServer.CallEitherAsync(mcp, tool, args, Ct);
        Assert.False(refused, $"{tool} was refused:\n{text}");
        return text;
    }
}

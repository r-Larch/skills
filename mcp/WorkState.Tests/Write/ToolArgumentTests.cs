using System.Text.Json;
using ModelContextProtocol.Client;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 01 §3.6 over the real transport: a call whose arguments cannot bind comes back as a refusal
/// that names the parameter and a call that binds — never as the SDK's "An error occurred invoking".
/// The first two cases are the calls a Foreman session actually made on 2026-10-22.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ToolArgumentTests(PostgresFixture fixture)
{
    [Fact]
    public async Task work_get_called_with_ref_is_refused_naming_refs_and_a_call_that_binds()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client, "work_get", new Dictionary<string, object?> { ["ref"] = "m2/1" }, Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("ref is not a parameter of work_get — the closest it declares is refs", text, StringComparison.Ordinal);
        Assert.Contains("→ work_get(refs:[\"…\"])", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The third live call: two names work_update does not declare, answered "OK updated (revision 3)"
    /// with nothing written, so the close guard refused next and the agent could not see why.
    /// </summary>
    [Fact]
    public async Task work_update_with_undeclared_arguments_is_refused_naming_each_and_writes_nothing()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);
        var before = (await session.World.ItemAsync("1.1")).Revision;

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client,
            "work_update",
            new Dictionary<string, object?> { ["ref"] = "1.1", ["check_all"] = true, ["evidence"] = "suite green" },
            Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Equal(2, RefusalShape.Conditions(text));
        Assert.Contains("check_all is not a parameter of work_update, which takes ref, criteria,", text, StringComparison.Ordinal);
        Assert.Contains("evidence is not a parameter of work_update", text, StringComparison.Ordinal);
        Assert.Equal(before, (await session.World.ItemAsync("1.1")).Revision);
    }

    [Theory]
    [InlineData("work_tree", "include_counts", "includeCounts")]
    [InlineData("work_history", "since_seq", "sinceSeq")]
    [InlineData("work_status", "reasn", "reason")]
    [InlineData("work_get", "ref", "refs")]
    public async Task An_undeclared_argument_names_the_closest_declared_parameter(string tool, string asked, string meant)
    {
        await using var session = await OpenAsync(ReadServer.ControllerToken);

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client, tool, new Dictionary<string, object?> { [asked] = "x" }, Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains($"{asked} is not a parameter of {tool} — the closest it declares is {meant}", text, StringComparison.Ordinal);
        Assert.Contains($"{meant}:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_tool_refuses_an_argument_it_does_not_declare()
    {
        await using var session = await OpenAsync(ReadServer.ControllerToken);
        var checkedTools = 0;

        foreach (var tool in await session.Client.ListToolsAsync(cancellationToken: Ct))
        {
            if (!tool.Name.StartsWith("work_", StringComparison.Ordinal))
            {
                continue;
            }

            var (text, refused) = await ReadServer.CallEitherAsync(
                session.Client, tool.Name, new Dictionary<string, object?> { ["zz_not_declared"] = 1 }, Ct);

            Assert.True(refused, $"{tool.Name}: {text}");
            RefusalShape.SelfCorrecting(text);
            Assert.Contains($"zz_not_declared is not a parameter of {tool.Name}, which takes", text, StringComparison.Ordinal);
            checkedTools++;
        }

        Assert.True(checkedTools >= 18, $"only {checkedTools} work_ tools listed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task work_update_that_sets_nothing_reports_nothing_changed_and_writes_nothing(int? expectedRevision)
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);
        var before = (await session.World.ItemAsync("1.1")).Revision;

        var arguments = new Dictionary<string, object?> { ["ref"] = "1.1", ["title"] = "  " };
        if (expectedRevision is { } revision)
        {
            arguments["expected_revision"] = revision;
        }

        var (text, refused) = await ReadServer.CallEitherAsync(session.Client, "work_update", arguments, Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("nothing changed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OK", text, StringComparison.Ordinal);
        Assert.Equal(before, (await session.World.ItemAsync("1.1")).Revision);
    }

    [Fact]
    public async Task work_update_that_sets_a_field_still_answers_OK_updated()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client,
            "work_update",
            new Dictionary<string, object?> { ["ref"] = "1.1", ["title"] = "Renamed", ["expected_revision"] = 1 },
            Ct);

        Assert.False(refused, text);
        Assert.StartsWith("OK  1.1  updated (revision 2)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task work_link_called_with_links_is_refused_naming_from_to_and_type()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client,
            "work_link",
            new Dictionary<string, object?>
            {
                ["links"] = new[] { new Dictionary<string, object?> { ["from"] = "1.1", ["to"] = "2", ["type"] = "blocks" } },
            },
            Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("from is required", text, StringComparison.Ordinal);
        Assert.Contains("to is required", text, StringComparison.Ordinal);
        Assert.Contains("type is required", text, StringComparison.Ordinal);
        Assert.Contains("work_link(from:\"…\", to:\"…\", type:\"…\")", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every tool, read off <c>tools/list</c> rather than listed here, so a tool added later is held to
    /// the same rule without anyone remembering to add it.
    /// </summary>
    [Fact]
    public async Task Every_tool_called_without_its_required_arguments_refuses_naming_each_one()
    {
        await using var session = await OpenAsync(ReadServer.ControllerToken);
        var checkedTools = 0;

        foreach (var tool in await session.Client.ListToolsAsync(cancellationToken: Ct))
        {
            var required = Required(tool.ProtocolTool.InputSchema);
            if (!tool.Name.StartsWith("work_", StringComparison.Ordinal) || required.Count == 0)
            {
                continue;
            }

            var (text, refused) = await ReadServer.CallEitherAsync(
                session.Client, tool.Name, new Dictionary<string, object?>(), Ct);

            Assert.True(refused, $"{tool.Name}: {text}");
            RefusalShape.SelfCorrecting(text);
            Assert.All(required, name => Assert.Contains($"{name} is required", text, StringComparison.Ordinal));
            Assert.Contains($"→ {tool.Name}(", text, StringComparison.Ordinal);
            checkedTools++;
        }

        Assert.True(checkedTools >= 10, $"only {checkedTools} tools declare a required argument");
    }

    [Fact]
    public async Task An_array_where_a_string_is_expected_is_refused_with_the_shape_it_takes()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client,
            "work_status",
            new Dictionary<string, object?> { ["ref"] = new[] { "1.1" }, ["to"] = "ready" },
            Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("ref takes a string, and an array was sent", text, StringComparison.Ordinal);
        Assert.Contains("→ work_status(ref:\"…\", to:\"…\")", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_object_where_an_array_is_expected_is_refused_with_the_shape_it_takes()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var (text, refused) = await ReadServer.CallEitherAsync(
            session.Client,
            "work_create",
            new Dictionary<string, object?>
            {
                ["items"] = new Dictionary<string, object?> { ["kind"] = "phase", ["title"] = "x" },
            },
            Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("items takes an array of objects, and an object was sent", text, StringComparison.Ordinal);
        Assert.Contains("→ work_create(items:[{…}])", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The binding backstop: these pass the schema check, which judges only containers, and are
    /// rejected by the binder itself — a scalar of the wrong kind, and a nested field of the wrong shape.
    /// </summary>
    [Theory]
    [InlineData("work_next", """{"limit":"ten"}""")]
    [InlineData("work_create", """{"items":[{"kind":"task","title":"x","verify_cmd":"dotnet test","criteria":"one"}]}""")]
    public async Task A_value_the_binder_rejects_is_refused_rather_than_the_generic_error(string tool, string json)
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(a => a.Key, a => (object?)a.Value);

        var (text, refused) = await ReadServer.CallEitherAsync(session.Client, tool, arguments, Ct);

        Assert.True(refused, text);
        RefusalShape.SelfCorrecting(text);
        Assert.Contains("binding refused it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred", text, StringComparison.Ordinal);
    }

    /// <summary>The one lenient reading: a lone string where a list of strings is expected.</summary>
    [Fact]
    public async Task A_lone_string_for_refs_binds_as_a_list_of_one()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var text = await ReadServer.CallAsync(
            session.Client, "work_get", new Dictionary<string, object?> { ["refs"] = "1.1" }, Ct);

        Assert.Contains("1.1", text, StringComparison.Ordinal);
        Assert.Contains("The task under test", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_with_every_declared_argument_and_one_with_optionals_omitted_both_still_bind()
    {
        await using var session = await OpenAsync(ReadServer.OrchestratorToken);

        var full = await ReadServer.CallAsync(
            session.Client,
            "work_get",
            new Dictionary<string, object?> { ["refs"] = new[] { "1.1" }, ["include"] = "deps" },
            Ct);
        var bare = await ReadServer.CallAsync(
            session.Client, "work_get", new Dictionary<string, object?> { ["refs"] = new[] { "1.1" } }, Ct);
        var none = await ReadServer.CallAsync(session.Client, "work_next", null, Ct);
        var tree = await ReadServer.CallAsync(
            session.Client,
            "work_tree",
            new Dictionary<string, object?> { ["scope"] = "run-a", ["depth"] = 2, ["status"] = "open", ["includeCounts"] = false },
            Ct);

        Assert.Contains("The task under test", full, StringComparison.Ordinal);
        Assert.Contains("The task under test", bare, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(none));
        Assert.Contains("run-a", tree, StringComparison.Ordinal);
    }

    /// <summary>
    /// The filter runs inside the authorization filters: a token that cannot see a tool is still
    /// forbidden it, and is not taught that tool's arguments on the way.
    /// </summary>
    [Fact]
    public async Task A_worker_calling_an_orchestrator_tool_with_bad_arguments_is_forbidden_not_taught()
    {
        await using var session = await OpenAsync(ReadServer.WorkerToken);

        var failure = await Record.ExceptionAsync(async () => await session.Client.CallToolAsync(
            "work_link",
            new Dictionary<string, object?> { ["links"] = new[] { "1.1" } },
            cancellationToken: Ct));

        Assert.NotNull(failure);
        Assert.Contains("forbidden", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REFUSED", failure.Message, StringComparison.Ordinal);
    }

    private static List<string> Required(JsonElement schema)
        => schema.TryGetProperty("required", out var names)
            ? [.. names.EnumerateArray().Select(n => n.GetString()!)]
            : [];

    private async Task<Session> OpenAsync(string token)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        return new Session(world, server, await server.ConnectAsync(token, Ct));
    }

    private sealed record Session(WriteWorld World, ReadServer Server, McpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}

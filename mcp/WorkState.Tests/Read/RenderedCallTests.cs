using System.Text.RegularExpressions;
using WorkState.Tests.AgentImport;
using WorkState.Tests.App;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// doc 04 §3 from the caller's side: a NEXT line or a refusal route is only a fix if it binds. Until
/// ws/m1 the brief's NEXT printed <c>work_get(ref:"2.8")</c> — a call the server itself refuses —
/// and a renderer test pinned that text.
/// <para>
/// This reads the shipped source rather than rendered output, because most templates only render in
/// a state that is costly to build (an expired claim, an ambiguous ref, a schema refusal) and a
/// template that is never driven is exactly the one that drifts. Every named argument of every
/// <c>work_xxx(</c> call written on a code line of WorkState.Core and WorkState.Mcp is checked
/// against the parameters that tool declares on <c>tools/list</c>. Positional shorthand
/// (<c>work_status(ref, "done")</c>) names no argument and is not judged.
/// </para>
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class RenderedCallTests(PostgresFixture fixture)
{
    [GeneratedRegex(@"\bwork_[a-z_]+\(")]
    private static partial Regex CallStart();

    [Fact]
    public async Task Every_named_argument_in_a_rendered_call_is_one_the_tool_declares()
    {
        var declared = await DeclaredAsync();
        var wrong = new List<string>();
        var judged = 0;

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match call in CallStart().Matches(lines[i]))
                {
                    var tool = call.Value[..^1];
                    var where = $"{Path.GetFileName(file)}:{i + 1}";

                    if (!declared.TryGetValue(tool, out var parameters))
                    {
                        wrong.Add($"{where}  {tool} is not a tool this server exposes");
                        continue;
                    }

                    foreach (var name in Named(lines[i], call.Index + call.Length))
                    {
                        judged++;
                        if (!parameters.Contains(name))
                        {
                            wrong.Add($"{where}  {tool}({name}:…) — {tool} takes {string.Join(", ", parameters)}");
                        }
                    }
                }
            }
        }

        Assert.True(judged > 150, $"only {judged} named arguments found; the scan is not reading the source");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// The names written at the call's own depth: <c>ref</c> and <c>criteria</c> in
    /// <c>work_update(ref:"2.4", criteria:[{id:"c2"}])</c>, never <c>id</c>.
    /// </summary>
    private static IEnumerable<string> Named(string line, int start)
    {
        var depth = 0;
        var expectName = true;

        for (var i = start; i < line.Length; i++)
        {
            var c = line[i];
            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    expectName = false;
                    continue;
                case ')' or ']' or '}':
                    if (depth == 0)
                    {
                        yield break;
                    }

                    depth--;
                    continue;
                case ',' when depth == 0:
                    expectName = true;
                    continue;
            }

            if (!expectName || char.IsWhiteSpace(c))
            {
                continue;
            }

            expectName = false;
            var end = i;
            while (end < line.Length && (char.IsAsciiLetterLower(line[end]) || line[end] == '_'))
            {
                end++;
            }

            if (end > i && end < line.Length && line[end] == ':')
            {
                yield return line[i..end];
            }

            i = end - 1;
        }
    }

    /// <summary>
    /// work_import is hidden from every session without the grant, so no text such a session can be
    /// shown may tell it to call work_import: the call is rendered only by the import writer, which
    /// only work_import reaches, and by that tool's own method.
    /// </summary>
    [Fact]
    public void A_rendered_work_import_call_comes_only_from_the_import_path()
    {
        var outside = new List<string>();

        foreach (var file in SourceFiles())
        {
            var name = Path.GetFileName(file);
            if (name == "WorkWriter.Import.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var inImportTool = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (name == "WorkWriteTools.cs" && lines[i].Contains("Task<CallToolResult> ImportAsync(", StringComparison.Ordinal))
                {
                    inImportTool = true;
                }
                else if (inImportTool && lines[i].Contains("Task<CallToolResult> ", StringComparison.Ordinal))
                {
                    inImportTool = false;
                }

                if (!inImportTool && lines[i].Contains("work_import(", StringComparison.Ordinal)
                    && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    outside.Add($"{name}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(outside.Count == 0, "work_import( rendered outside the import path:\n" + string.Join("\n", outside));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WorkState.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        return new[] { "WorkState.Core", "WorkState.Mcp" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root.FullName, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The static controller's tools/list, unioned with a minted controller's that carries every
    /// grant: some tools (work_import) are shown only to granted sessions, never to a static token.
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> DeclaredAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var (_, minted) = await ImportVisibilityTests.MintAsync(
            server, ReadServer.ControllerToken, new { name = "aw-rendered", role = "controller", grants = new[] { "import" } });

        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var token in new[] { ReadServer.ControllerToken, ImportVisibilityTests.Token(minted) })
        {
            await using var client = await server.ConnectAsync(token, Ct);
            foreach (var t in await client.ListToolsAsync(cancellationToken: Ct))
            {
                declared[t.Name] = t.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
                    ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                    : [];
            }
        }

        return declared;
    }
}

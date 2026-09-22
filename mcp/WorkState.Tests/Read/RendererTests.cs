using System.Text.RegularExpressions;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using Xunit;

namespace WorkState.Tests.Read;

/// <summary>
/// doc 06 §7. The renderer is domain logic, so it is tested without a database and without a web
/// host — these are the cheap tests, and they are the only defence against drift back toward prose.
/// </summary>
public sealed class RendererTests
{
    /// <summary>Canonical 8-4-4-4-12. If one of these ever reaches a rendering, doc 06 §1 rule 2 is broken.</summary>
    public static readonly Regex Uuid = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    [Fact]
    public void Every_row_starts_with_the_ref_and_holds_its_column_positions()
    {
        var row = BoardRenderer.Row(new BoardItem(
            "2.3", "task", ItemStatus.Done, "EntityProjector", Evidence: "✓12/12", Flags: ["a1b2c3d"]));

        Assert.StartsWith("2.3", row, StringComparison.Ordinal);
        Assert.Equal("task", row.Substring(Columns.KindAt, 4));
        Assert.Equal("done", row.Substring(Columns.StatusAt, 4));
        Assert.Equal("EntityProjector", row.Substring(Columns.TitleAt, 15));
        Assert.Equal("✓12/12", row.Substring(Columns.EvidenceAt, 6));
        Assert.Equal("a1b2c3d", row[Columns.FlagsAt..]);
    }

    [Fact]
    public void A_six_character_ref_and_a_full_width_title_still_line_up()
    {
        var row = BoardRenderer.Row(new BoardItem(
            "F-0007", "finding", ItemStatus.Open, new string('x', Columns.Title)));

        Assert.StartsWith("F-0007", row, StringComparison.Ordinal);
        Assert.Equal("find", row.Substring(Columns.KindAt, 4));
        Assert.Equal(new string('x', Columns.Title), row.Substring(Columns.TitleAt, Columns.Title));
    }

    [Fact]
    public void A_title_longer_than_the_column_is_clipped_with_an_ellipsis()
    {
        var row = BoardRenderer.Row(new BoardItem("2.4", "task", ItemStatus.Open, new string('y', 80)));
        var title = row.Substring(Columns.TitleAt, Columns.Title);

        Assert.EndsWith("…", title, StringComparison.Ordinal);
        Assert.Equal(Columns.Title, title.Length);
    }

    [Fact]
    public void An_in_progress_row_carries_its_age_and_a_clean_row_carries_no_flags()
    {
        var doing = BoardRenderer.Row(new BoardItem(
            "2.4", "task", ItemStatus.InProgress, "Run service", DoingFor: TimeSpan.FromMinutes(43)));

        Assert.Equal("doing 43m", doing.Substring(Columns.StatusAt, 9));
        Assert.Equal(doing.TrimEnd(), doing);
        Assert.EndsWith("—", doing, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tree_indents_two_spaces_a_level_and_keeps_the_ref_as_the_first_token()
    {
        var rows = BoardRenderer.Tree(
        [
            new TreeItem(new BoardItem("gaia", "project", ItemStatus.Open, "Gaia"), 0),
            new TreeItem(new BoardItem("run-a", "run", ItemStatus.InProgress, "Entity store"), 1, "12/24 done"),
            new TreeItem(new BoardItem("2.7", "task", ItemStatus.Done, "jsonb"), 3),
        ]).Split('\n');

        Assert.StartsWith("gaia ", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("  run-a", rows[1], StringComparison.Ordinal);
        Assert.EndsWith("12/24 done", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("      2.7", rows[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_result_emits_no_attention_header_at_all()
    {
        Assert.Equal(string.Empty, Attention.Block([]));
        Assert.Equal("body", Attention.Append("body", []));
    }

    [Fact]
    public void The_attention_block_caps_at_five_lines_and_every_line_names_a_call()
    {
        var lines = Enumerable.Range(0, 9)
            .Select(i => new AttentionLine((AttentionRank)(i % 6), $"thing {i} is wrong", $"work_status(ref:\"{i}\")"))
            .ToList();

        var block = Attention.Block(lines).Split('\n');

        Assert.StartsWith("--- attention (5) ---", block[0], StringComparison.Ordinal);
        Assert.Equal(6, block.Length);
        Assert.All(block[1..], line =>
        {
            Assert.Contains(" — ", line, StringComparison.Ordinal);
            Assert.Matches(@"— work_[a-z]+\(", line);
        });
    }

    [Fact]
    public void The_attention_block_drops_the_cheapest_lines_first()
    {
        var block = Attention.Block(
        [
            new AttentionLine(AttentionRank.StaleInProgress, "stale", "work_status(ref:\"a\")"),
            new AttentionLine(AttentionRank.ExpiredClaim, "expired", "work_status(ref:\"b\")"),
        ]).Split('\n');

        Assert.Contains("expired", block[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_brief_renders_a_callable_NEXT_and_never_an_empty_attention_header()
    {
        var text = BriefRenderer.Render(Sample([]));

        Assert.Contains("RUN entity-store", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^NEXT   work_get\(ref:""2\.8""\)", text);
        Assert.DoesNotContain("--- attention", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(Uuid, text);
    }

    /// <summary>
    /// U9.1. The block below BINDING prints <c>A3</c>; BINDING printed the text and nothing else, so
    /// a rule could be read but never cited, questioned or retired.
    /// </summary>
    [Fact]
    public void A_binding_line_carries_its_ref_between_the_marker_and_the_rule()
    {
        var line = BriefRenderer.Render(Sample([]))
            .Split('\n')
            .Single(l => l.Contains("never modify", StringComparison.Ordinal));

        Assert.Equal("  ! C1  never modify Gaia.Web/Core/Rag/** — ask the owner", line);
        Assert.True(line.Length <= Columns.Width, $"the line is {line.Length} columns");
    }

    [Fact]
    public void The_brief_stays_inside_its_line_budget_and_names_what_it_left_out()
    {
        var text = BriefRenderer.Render(Sample([]) with
        {
            BindingTotal = 40,
            AssumptionTotal = 30,
            NoteTotal = 25,
            AddressedTotal = 25,
        });

        Assert.True(text.Split('\n').Length <= 35, $"the brief is {text.Split('\n').Length} lines");
        Assert.Contains("+39 more — read work://workspace/conventions", text, StringComparison.Ordinal);
        Assert.Contains("+29 more — work_threads(", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FINDINGS-live F-L3, the half the count was hiding: the real run rendered
    /// <c>ADDRESSED TO YOU (140)</c> over 140 notes, not one of which named a target.
    /// </summary>
    [Fact]
    public void The_note_header_says_addressed_only_when_a_note_names_an_item()
    {
        var addressed = BriefRenderer.Render(Sample([]) with { NoteTotal = 5, AddressedTotal = 2 });

        Assert.Contains("ADDRESSED TO YOU (2)   — these render into 2.8's brief", addressed, StringComparison.Ordinal);
        Assert.Contains("+4 more unconsumed in scope — work_threads(", addressed, StringComparison.Ordinal);

        var broadcast = BriefRenderer.Render(Sample([]) with
        {
            NoteTotal = 140,
            AddressedTotal = 0,
            AddressedTo = null,
        });

        Assert.DoesNotContain("ADDRESSED TO YOU", broadcast, StringComparison.Ordinal);
        Assert.Contains("NOTES IN SCOPE (140)", broadcast, StringComparison.Ordinal);
        Assert.Contains("none names an item", broadcast, StringComparison.Ordinal);

        var header = broadcast.Split('\n').Single(l => l.StartsWith("NOTES IN SCOPE", StringComparison.Ordinal));
        Assert.True(header.Length <= Columns.Width, $"the header is {header.Length} columns");
    }

    [Fact]
    public void Onboarding_explains_a_verify_command_that_can_fail_and_names_one_call()
    {
        var text = BriefRenderer.Onboarding();

        Assert.Contains("NEXT   work_create(", text, StringComparison.Ordinal);
        Assert.Contains("was already", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(Uuid, text);
    }

    [Fact]
    public void No_uuid_survives_any_renderer_even_when_one_is_handed_in_as_a_title()
    {
        var id = Guid.CreateVersion7().ToString();

        var row = BoardRenderer.Row(new BoardItem("2.3", "task", ItemStatus.Open, id));
        var tree = BoardRenderer.TreeRow(new TreeItem(new BoardItem("2.3", "task", ItemStatus.Open, id), 1));

        // The title column is 34 wide, so even a uuid handed straight in comes back clipped.
        Assert.DoesNotMatch(Uuid, row);
        Assert.DoesNotMatch(Uuid, tree);
    }

    private static BriefModel Sample(IReadOnlyList<AttentionLine> attention)
        => new(
            RunRef: "entity-store",
            ProjectRef: "gaia",
            Branch: "feat/entity-store",
            Head: "7cd34161",
            Goal: "The voice agent answers from a curated entity store instead of page-chunked PDFs.",
            Where: "phase 2 of 9 — Ingestion. 6 of 8 tasks done.",
            Next: "work_get(ref:\"2.8\")  then  work_status(ref:\"2.8\", to:\"in_progress\")",
            Binding: [new BindingLine("C1", "never modify Gaia.Web/Core/Rag/**", true, "ask the owner")],
            BindingTotal: 1,
            Assumptions: [new AssumptionLine("A3", "DSS jsonexport reachable", "probed", "medium", "3.1")],
            AssumptionTotal: 1,
            Notes: ["facets must be replaced per entity per run"],
            NoteTotal: 1,
            AddressedTotal: 1,
            AddressedTo: "2.8",
            LastVerify: new VerifyLine("dotnet test --filter Importer", "181/181", "orchestrator", TimeSpan.FromMinutes(6), true),
            Attention: attention);
}

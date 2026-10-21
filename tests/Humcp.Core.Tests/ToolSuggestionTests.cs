using System.Text.Json;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// What the model is told about the tools, and how its answer is read back (Phase 11). The model is
/// not here — <c>Humcp.Inference.Tests</c> asks it — but everything around it is, and the parser is
/// what decides that the answer can only ever be tools that exist.
/// </summary>
public class ToolSuggestionTests
{
    private static readonly IReadOnlyList<ToolSearchEntry> Catalogue =
    [
        Entry("time", "get_current_time", "Get current time in a specific timezone."),
        Entry("time", "convert_time", "Convert time between timezones."),
        Entry("google-maps", "maps_elevation", "Get elevation data for locations on the earth."),
        Entry("1password", "get_item", "Reads one item."),
    ];

    [Fact]
    public void The_catalogue_is_one_line_per_tool_with_server_and_name()
    {
        var text = ToolSuggestionPrompt.Catalogue(Catalogue);

        Assert.Contains("time/get_current_time: Get current time in a specific timezone.\n", text);
        Assert.Contains("google-maps/maps_elevation: Get elevation data", text);
        Assert.DoesNotContain("The request", text);
    }

    [Fact]
    public void The_question_carries_the_request_and_asks_for_ids_only()
    {
        var text = ToolSuggestionPrompt.Question("how high above sea level is Denver");

        Assert.Contains("\"how high above sea level is Denver\"", text);
        Assert.Contains("3 tool ids", text);
    }

    [Theory]
    [InlineData("google-maps/maps_elevation\ntime/get_current_time\ntime/convert_time")]
    [InlineData("1. google-maps/maps_elevation\n2. time/get_current_time\n3. time/convert_time")]
    [InlineData("`google-maps/maps_elevation`\n- time/get_current_time\n* time/convert_time.")]
    public void Ids_are_read_back_however_the_model_dressed_them(string answer)
    {
        var picked = ToolSuggestionPrompt.Parse(answer, Catalogue);

        Assert.Equal(["maps_elevation", "get_current_time", "convert_time"], picked.Select(p => p.Tool.Name));
    }

    [Fact]
    public void A_tool_that_does_not_exist_is_dropped_not_invented()
    {
        var picked = ToolSuggestionPrompt.Parse("weather/get_forecast\ntime/get_current_time\nnothing at all", Catalogue);

        Assert.Equal("get_current_time", Assert.Single(picked).Tool.Name);
    }

    [Fact]
    public void A_repeated_tool_counts_once_and_no_more_than_three_are_kept()
    {
        var picked = ToolSuggestionPrompt.Parse(
            "time/convert_time\ntime/convert_time\ntime/get_current_time\ngoogle-maps/maps_elevation\n1password/get_item", Catalogue);

        Assert.Equal(["convert_time", "get_current_time", "maps_elevation"], picked.Select(p => p.Tool.Name));
    }

    [Fact]
    public void A_list_number_is_stripped_but_a_server_name_that_starts_with_a_digit_is_not()
        => Assert.Equal("get_item", Assert.Single(ToolSuggestionPrompt.Parse("1. 1password/get_item", Catalogue)).Tool.Name);

    [Fact]
    public void The_fingerprint_changes_when_any_tool_does()
    {
        var before = ToolSuggestionPrompt.Fingerprint(Catalogue);
        var changed = Catalogue.Take(3).Append(Entry("1password", "get_item", "Reads one item, now differently.")).ToList();

        Assert.Equal(before, ToolSuggestionPrompt.Fingerprint([.. Catalogue]));
        Assert.NotEqual(before, ToolSuggestionPrompt.Fingerprint(changed));
    }

    [Fact]
    public async Task The_lexical_suggester_is_Phase_8_s_search_behind_the_interface()
    {
        IToolSuggester suggester = new LexicalToolSuggester();

        var found = await suggester.SuggestAsync("convert ", Catalogue, default);

        Assert.Equal("convert_time", found[0].Tool.Name);
    }

    private static ToolSearchEntry Entry(string server, string name, string summary)
        => new(server, server, new ToolInfo(name, NameHumanizer.DisplayName(null, name), summary, summary,
            OperationSafety.Unstated, FormBuilder.Build(JsonDocument.Parse("""{"type":"object"}""").RootElement)));
}

/// <summary>What a summary is made from and how it is labelled (Phase 11).</summary>
public class ResultSummaryTests
{
    [Fact]
    public void Every_text_part_is_read_and_pictures_are_not()
    {
        var outcome = new ToolCallOutcome(false,
        [
            new TextResultBlock("First part."),
            new MediaResultBlock(MediaKind.Image, "image/png", new byte[] { 1, 2, 3 }),
            new ResourceTextResultBlock("file:///notes.txt", "text/plain", "Second part."),
        ]);

        Assert.Equal("First part.\n\nSecond part.", ResultSummary.Source(outcome));
    }

    [Fact]
    public void A_short_result_is_not_offered_a_summary()
    {
        Assert.False(ResultSummary.IsWorthSummarising(new ToolCallOutcome(false, [new TextResultBlock("Short.")])));
        Assert.True(ResultSummary.IsWorthSummarising(new ToolCallOutcome(false,
            [new TextResultBlock(new string('a', ResultSummary.WorthSummarising))])));
    }

    [Fact]
    public void A_result_too_long_to_send_is_cut_and_the_summary_says_so()
    {
        var outcome = new ToolCallOutcome(false, [new TextResultBlock(new string('a', ResultSummary.MaxCharacters + 10))]);

        var (request, cut) = ResultSummary.Request("fetch", outcome);

        Assert.True(cut);
        Assert.Contains("A tool called fetch returned the result below.", request);
        Assert.Contains("first 32,000 characters", ResultSummary.Generated("It is about a.", cut));
    }

    [Fact]
    public void A_summary_is_labelled_before_a_word_of_it()
        => Assert.StartsWith("Generated by the local model, and it can be wrong: The forecast",
            ResultSummary.Generated("The forecast is rain.", cut: false));
}

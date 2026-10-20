using System.Text.Json;

namespace Humcp.Schema.Tests;

/// <summary>
/// JSON a server sent as text (humcp 1.1). Most servers send no structured content at all; they
/// serialise their answer into a text part, and 1.0 showed it as one line of JSON.
/// </summary>
public class JsonTextTests
{
    private static readonly string NewLine = char.ConvertFromUtf32(10);

    private static string Lines(params string[] lines) => string.Join(NewLine, lines);

    // ------------------------------------------------------------------ what is read

    [Fact]
    public void An_object_sent_as_text_is_read_as_json()
    {
        Assert.True(JsonText.TryRead("""{"elevation":[87]}""", out var title, out var value));

        Assert.Equal("", title);
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
    }

    [Fact]
    public void An_array_sent_as_text_is_read_as_json()
    {
        Assert.True(JsonText.TryRead("""  [ { "a": 1 } ]  """, out _, out var value));
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
    }

    [Fact]
    public void A_title_in_front_of_the_json_becomes_its_heading()
    {
        // The MLB server's shape: a title, a blank line, and then indented JSON.
        Assert.True(JsonText.TryRead(Lines("Tracked Teams", "", "[", "  { \"key\": \"TOR\" }", "]"),
            out var title, out var value));

        Assert.Equal("Tracked Teams", title);
        Assert.Equal(1, value.GetArrayLength());
    }

    [Fact]
    public void A_title_needs_no_blank_line_and_loses_its_colon()
    {
        Assert.True(JsonText.TryRead(Lines("Results:", "{ \"a\": 1 }"), out var title, out _));
        Assert.Equal("Results", title);
    }

    // ------------------------------------------------------------------ what stays text

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    public void A_bare_value_stays_text_because_it_is_already_readable(string text)
    {
        Assert.False(JsonText.TryRead(text, out _, out _));
    }

    [Fact]
    public void Prose_stays_text()
    {
        Assert.False(JsonText.TryRead("It is 22.5 degrees and cloudy in Chicago.", out _, out _));
        Assert.False(JsonText.TryRead("", out _, out _));
        Assert.False(JsonText.TryRead(null, out _, out _));
    }

    [Fact]
    public void Json_with_anything_after_it_stays_text()
    {
        // The JSON has to run to the end. Half a result read as a tree and half dropped would be
        // worse than the whole of it as text.
        Assert.False(JsonText.TryRead(Lines("{ \"a\": 1 }", "", "Data courtesy of somebody."), out _, out _));
    }

    [Fact]
    public void Json_cut_short_stays_text()
    {
        // What a result past TextSanitizer.ResultLength looks like: the end is missing.
        Assert.False(JsonText.TryRead("""{ "hourly": { "time": [ "2026-10-19T00:00", """, out _, out _));
    }

    [Fact]
    public void More_than_a_short_title_in_front_stays_text()
    {
        var text = Lines("One.", "Two.", "Three.", "Four.", "{ \"a\": 1 }");
        Assert.False(JsonText.TryRead(text, out _, out _));

        Assert.True(JsonText.TryRead(Lines("One.", "Two.", "Three.", "{ \"a\": 1 }"), out _, out _));
    }

    [Fact]
    public void A_long_paragraph_in_front_stays_text()
    {
        Assert.False(JsonText.TryRead(Lines(new string('x', 300), "{ \"a\": 1 }"), out _, out _));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_not_json()
    {
        Assert.False(JsonText.TryRead("""{ "a": 1, }""", out _, out _));
        Assert.False(JsonText.TryRead("""{ "a": 1 /* one */ }""", out _, out _));
    }

    // ------------------------------------------------------------------ real servers

    [Theory]
    [InlineData("weather_forecast")]
    [InlineData("geocoding")]
    [InlineData("elevation")]
    public void Every_open_meteo_answer_is_read_as_json(string tool)
    {
        Assert.True(JsonText.TryRead(ResultFixtures.Text("open-meteo", tool), out var title, out _));
        Assert.Equal("", title);
    }

    [Theory]
    [InlineData("get_tracked_teams", "Tracked Teams")]
    [InlineData("get_current_season", "Season 2026")]
    public void The_mlb_servers_json_is_read_with_its_title(string tool, string expected)
    {
        Assert.True(JsonText.TryRead(ResultFixtures.Text("mlb", tool), out var title, out _));
        Assert.Equal(expected, title);
    }

    [Theory]
    [InlineData("get_standings")]
    [InlineData("get_team_roster")]
    public void The_mlb_servers_prose_stays_prose(string tool)
    {
        Assert.False(JsonText.TryRead(ResultFixtures.Text("mlb", tool), out _, out _));
    }
}

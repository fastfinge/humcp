using System.Text.Json;

namespace Humcp.Schema.Tests;

/// <summary>
/// The result renderer's rules, tested where they live: in a pure library, with no window anywhere
/// (ROADMAP §6.3). What a value is called, when a string wants a window of its own, what counts as
/// a table, and what a result says when it is too big to show — all of it is decided here.
/// </summary>
public class ResultBuilderTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ResultNode Build(string value, string? schema = null, ResultBuildOptions? options = null) =>
        ResultBuilder.Build(schema is null ? null : Json(schema), Json(value), "Result", options);

    // ------------------------------------------------------------------ single values

    [Fact]
    public void A_string_is_one_value_read_as_its_name_and_itself()
    {
        var node = Build("\"cerulean\"");

        Assert.Equal(ResultShape.Value, node.Shape);
        Assert.Equal("cerulean", node.Text);
        Assert.Equal("text", node.TypeName);
        Assert.Equal("Result, cerulean", node.AccessibleName);
    }

    [Theory]
    [InlineData("42", "number", "42")]
    [InlineData("-1.5", "number", "-1.5")]
    [InlineData("true", "true or false", "true")]
    [InlineData("false", "true or false", "false")]
    [InlineData("null", "null", "null")]
    public void Every_kind_of_value_says_what_it_is(string json, string type, string text)
    {
        var node = Build(json);

        Assert.Equal(type, node.TypeName);
        Assert.Equal(text, node.Text);
    }

    [Fact]
    public void An_empty_string_says_empty_rather_than_nothing_at_all()
    {
        // "Notes," followed by silence is indistinguishable from a control that failed to speak.
        Assert.Equal("Result, empty", Build("\"\"").AccessibleName);
    }

    [Fact]
    public void A_long_string_wants_a_window_rather_than_a_line()
    {
        var node = Build(JsonSerializer.Serialize(new string('x', 500)));

        Assert.True(node.IsLongText);
        Assert.Equal("Result, 500 characters", node.AccessibleName);
        Assert.Equal(500, node.Text.Length);
    }

    [Fact]
    public void A_string_with_lines_in_it_wants_a_window_however_short_it_is()
    {
        // §7.19: line structure is the only structure a result like this has, and a tree node is
        // one line by construction.
        var text = "one" + char.ConvertFromUtf32(10) + "two";
        var node = ResultBuilder.Build(null, JsonSerializer.SerializeToElement(text));

        Assert.True(node.IsLongText);
        Assert.Equal(text.ReplaceLineEndings(), node.Text.ReplaceLineEndings());
    }

    [Fact]
    public void Values_are_sanitised_on_the_way_out_like_everything_else_a_server_sends()
    {
        // Built from a code point rather than pasted in, the way the fixture server does it: a
        // result is the most untrusted text in the application (§4, pitfall 5).
        var bell = char.ConvertFromUtf32(7);
        var node = ResultBuilder.Build(null, JsonSerializer.SerializeToElement("before" + bell + "after"));

        // A control character becomes a space rather than vanishing, which is TextSanitizer's rule
        // and not this file's: what matters here is that nothing a synthesiser could choke on
        // survives the crossing.
        Assert.Equal("before after", node.Text);
        Assert.DoesNotContain(node.Text, c => char.IsControl(c));
    }

    // ------------------------------------------------------------------ objects

    [Fact]
    public void An_object_becomes_named_fields_with_the_names_made_pronounceable()
    {
        var node = Build("""{ "first_name": "Ada", "yearOfBirth": 1815 }""");

        Assert.Equal(ResultShape.Properties, node.Shape);
        Assert.Equal("Result, 2 fields", node.AccessibleName);
        Assert.Equal(["First name", "Year of birth"], node.Children.Select(c => c.Label));
        Assert.Equal(["first_name", "yearOfBirth"], node.Children.Select(c => c.Path));
        Assert.Equal("First name, Ada", node.Children[0].AccessibleName);
    }

    [Fact]
    public void The_schema_supplies_the_names_and_descriptions_the_data_cannot()
    {
        var node = Build(
            """{ "temperature": 22.5, "conditions": "cloudy" }""",
            """
            {
              "type": "object",
              "properties": {
                "temperature": { "type": "number", "title": "Temperature in celsius",
                                 "description": "Measured at the airport. Updated hourly." }
              }
            }
            """);

        var field = node.Children[0];

        Assert.Equal("Temperature in celsius", field.Label);
        Assert.Equal("Measured at the airport.", field.HelpText);
    }

    [Fact]
    public void A_schema_that_describes_something_else_entirely_costs_nothing()
    {
        // A server is free to publish an output schema and then answer with something else. The
        // value is what arrived, so the value wins and the schema is simply not consulted.
        var node = Build("""{ "surprise": true, "second": 1 }""", """{ "type": "string" }""");

        Assert.Equal(ResultShape.Properties, node.Shape);
        Assert.Equal("Surprise", node.Children[0].Label);
    }

    // ------------------------------------------------------------------ tables

    [Fact]
    public void An_array_of_objects_is_a_table()
    {
        var node = Build("""
            [ { "name": "Ada", "role": "engineer" },
              { "name": "Grace", "role": "admiral" } ]
            """);

        Assert.Equal(ResultShape.Table, node.Shape);
        Assert.Equal(["Name", "Role"], node.Columns.Select(c => c.Label));
        Assert.Equal(["name", "role"], node.Columns.Select(c => c.Name));
        Assert.Equal(2, node.TotalRows);
        Assert.Equal("Result, table, 2 rows, 2 columns", node.AccessibleName);
        Assert.Equal("Grace", node.Rows[1].Cells[0].Text);
        Assert.Equal(2, node.Rows[1].Number);
    }

    [Fact]
    public void Columns_follow_the_schema_first_and_then_whatever_else_arrived()
    {
        // The schema's order is the order the server thinks its properties read best; a property it
        // did not mention still arrived, and the data is the authority on that.
        var node = Build(
            """[ { "extra": 1, "b": 2, "a": 3 } ]""",
            """
            {
              "type": "array",
              "items": { "type": "object", "properties": { "a": {}, "b": {} } }
            }
            """);

        Assert.Equal(["a", "b", "extra"], node.Columns.Select(c => c.Name));
    }

    [Fact]
    public void A_column_the_schema_names_but_nothing_uses_is_not_a_column()
    {
        var node = Build(
            """[ { "a": 1 } ]""",
            """{ "type": "array", "items": { "type": "object", "properties": { "a": {}, "unused": {} } } }""");

        Assert.Equal(["a"], node.Columns.Select(c => c.Name));
    }

    [Fact]
    public void A_missing_property_is_an_empty_cell_rather_than_a_missing_one()
    {
        var node = Build("""[ { "a": 1, "b": 2 }, { "a": 3 } ]""");

        Assert.Equal(2, node.Rows[1].Cells.Count);
        Assert.Equal("", node.Rows[1].Cells[1].Text);
    }

    [Fact]
    public void A_cell_holding_a_whole_value_keeps_it_and_says_how_much_of_it_there_is()
    {
        var node = Build("""[ { "name": "Ada", "notes": ["one", "two", "three"] } ]""");

        var cell = node.Rows[0].Cells[1];

        Assert.True(cell.HasDetail);
        Assert.Equal("3 items", cell.Text);
        Assert.Equal(ResultShape.List, cell.Detail!.Shape);
        Assert.Equal("[0].notes[2]", cell.Detail.Children[2].Path);
    }

    [Fact]
    public void An_array_of_anything_else_is_a_list_and_not_a_table()
    {
        var scalars = Build("""["red", "green", "blue"]""");

        Assert.Equal(ResultShape.List, scalars.Shape);
        Assert.Equal("Result, 3 items", scalars.AccessibleName);
        Assert.Equal("Item 2, green", scalars.Children[1].AccessibleName);
        Assert.Equal("[1]", scalars.Children[1].Path);

        // One object among strings has no columns, and a grid with a row-shaped hole in it is
        // worse than a list.
        var mixed = Build("""["red", { "name": "green" }]""");
        Assert.Equal(ResultShape.List, mixed.Shape);
    }

    [Fact]
    public void An_empty_array_is_a_list_of_nothing_rather_than_a_table_of_nothing()
    {
        var node = Build("[]");

        Assert.Equal(ResultShape.List, node.Shape);
        Assert.Equal("Result, 0 items", node.AccessibleName);
    }

    // ------------------------------------------------------------------ references

    [Fact]
    public void A_reference_into_defs_is_followed_for_its_names()
    {
        var node = Build(
            """{ "filter": { "contains": "text" } }""",
            """
            {
              "type": "object",
              "properties": { "filter": { "$ref": "#/$defs/f" } },
              "$defs": {
                "f": {
                  "type": "object",
                  "title": "Report filter",
                  "properties": { "contains": { "type": "string", "title": "Contains" } }
                }
              }
            }
            """);

        // The wrapper of one comes off, so the node the tree opens on is the filter itself.
        Assert.Equal("Report filter", node.Label);
        Assert.Equal("Contains", Assert.Single(node.Children).Label);
    }

    [Fact]
    public void A_schema_that_refers_to_itself_costs_a_label_and_nothing_else()
    {
        // Unlike a form, a result has a finite value in front of it: the recursion is bounded by
        // the data. A cycle in the schema stops the names, not the rendering.
        var node = Build(
            """{ "name": "top", "child": { "name": "middle", "child": { "name": "bottom" } } }""",
            """
            {
              "$ref": "#/$defs/branch",
              "$defs": {
                "branch": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "title": "Branch name" },
                    "child": { "$ref": "#/$defs/branch" }
                  }
                }
              }
            }
            """);

        Assert.Equal("Branch name", node.Children[0].Label);
        Assert.Equal("name", node.Children[1].Children[0].Path.Split('.')[^1]);
    }

    [Fact]
    public void A_reference_out_of_the_document_is_not_followed()
    {
        // §5: a schema is a third-party document, and rendering a result must not become an
        // outbound request to an address that document chose.
        var node = Build(
            """{ "a": 1, "b": 2 }""",
            """{ "type": "object", "properties": { "a": { "$ref": "https://example.com/s.json" } } }""");

        Assert.Equal("A", node.Children[0].Label);
    }

    // ------------------------------------------------------------------ limits, and saying so

    [Fact]
    public void Nesting_past_the_limit_becomes_json_that_says_why()
    {
        var deep = "{ \"a\":" + string.Concat(Enumerable.Repeat("{ \"a\":", 20)) + "1" +
                   new string('}', 21);

        var node = Build(deep);
        var bottom = node.SelfAndDescendants().Single(n => n.DegradedReason is not null);

        Assert.Equal("it is nested deeper than a window can show", bottom.DegradedReason);
        Assert.True(bottom.IsTruncated);
        Assert.Contains("\"a\"", bottom.Text);
    }

    [Fact]
    public void A_table_bigger_than_the_limit_reports_its_real_size()
    {
        var rows = Enumerable.Range(1, 40).Select(i => $$"""{ "i": {{i}} }""");
        var node = Build($"[{string.Join(",", rows)}]", options: new ResultBuildOptions { MaxRows = 10 });

        Assert.Equal(10, node.Rows.Count);
        Assert.Equal(40, node.TotalRows);
        Assert.True(node.IsTruncated);

        // The number the user hears is the real one, not the number we chose to show.
        Assert.Equal("Result, table, 40 rows, 1 column", node.AccessibleName);
    }

    [Fact]
    public void Too_many_columns_are_left_out_and_the_node_says_so()
    {
        var cells = Enumerable.Range(1, 40).Select(i => $"\"c{i:D2}\": {i}");
        var node = Build($"[{{{string.Join(",", cells)}}}]", options: new ResultBuildOptions { MaxColumns = 5 });

        Assert.Equal(5, node.Columns.Count);
        Assert.True(node.IsTruncated);
    }

    [Fact]
    public void A_result_larger_than_the_node_budget_stops_and_admits_it()
    {
        var rows = Enumerable.Range(1, 200).Select(i => $$"""{ "a": { "b": {{i}} } }""");
        var node = Build($"[{string.Join(",", rows)}]", options: new ResultBuildOptions { MaxNodes = 50 });

        Assert.True(node.IsTruncated);
    }

    [Fact]
    public void There_is_no_value_it_refuses_to_render()
    {
        // FormBuilder's invariant, from the other end of the call.
        Assert.Equal(ResultShape.Value, ResultBuilder.Build(null, default).Shape);
        Assert.Equal(ResultShape.Properties, Build("{}").Shape);
        Assert.Equal(ResultShape.Value, Build("null").Shape);
        Assert.Equal(ResultShape.Value, Build("\"x\"", "true").Shape);
    }

    // ------------------------------------------------------------------ the duplicate wrapper

    [Fact]
    public void Structured_content_that_is_the_text_again_is_recognised()
    {
        // 13 of the corpus's 25 output schemas are { "content": { "type": "string" } }, filled with
        // the string the server already sent as text. Rendering both says everything twice.
        Assert.True(ResultBuilder.RepeatsText(Json("""{ "content": "one\ntwo" }"""), "one\ntwo"));
        Assert.True(ResultBuilder.RepeatsText(Json("""{ "anything": "hello" }"""), "  hello  "));
        Assert.True(ResultBuilder.RepeatsText(Json("\"hello\""), "hello"));
    }

    [Fact]
    public void Structured_content_that_says_anything_else_is_not_a_duplicate()
    {
        Assert.False(ResultBuilder.RepeatsText(Json("""{ "content": "one" }"""), "two"));
        Assert.False(ResultBuilder.RepeatsText(Json("""{ "a": "x", "b": "y" }"""), "x"));
        Assert.False(ResultBuilder.RepeatsText(Json("""{ "count": 3 }"""), "3"));
        Assert.False(ResultBuilder.RepeatsText(Json("""{ "content": "x" }"""), ""));
    }

    [Fact]
    public void Structured_content_sent_again_as_json_text_is_recognised_however_it_is_indented()
    {
        // The specification asks a server with structured content to send it serialised as text as
        // well. 1.0 compared strings, so every server that did as it was asked was shown twice.
        var structured = Json("""{ "city": "Toronto", "temperature": 12.8 }""");

        Assert.True(ResultBuilder.RepeatsText(structured, """{"city":"Toronto","temperature":12.8}"""));
        Assert.True(ResultBuilder.RepeatsText(structured, "{\n  \"temperature\": 12.8,\n  \"city\": \"Toronto\"\n}"));

        // Or the list inside the wrapper, sent bare.
        Assert.True(ResultBuilder.RepeatsText(Json("""{ "entities": [ { "a": 1 } ] }"""), """[{"a":1}]"""));

        Assert.False(ResultBuilder.RepeatsText(structured, """{"city":"Toronto","temperature":13}"""));
    }

    // ------------------------------------------------------------------ tables stored by column

    [Fact]
    public void An_object_of_equal_lists_is_a_table_one_row_per_position()
    {
        // Open-Meteo's shape: the 2nd time and the 2nd temperature belong together, and as two
        // lists side by side nothing on screen said so.
        var node = Build("""
            { "time": ["00:00", "01:00", "02:00"], "temperature_2m": [11.1, 10.4, 9.9], "rain": [0, 0, null] }
            """);

        Assert.Equal(ResultShape.Table, node.Shape);
        Assert.Equal(["time", "temperature_2m", "rain"], node.Columns.Select(c => c.Name));
        Assert.Equal("Temperature 2 m", node.Columns[1].Label);
        Assert.Equal(3, node.TotalRows);
        Assert.Equal(["01:00", "10.4", "0"], node.Rows[1].Cells.Select(c => c.Text));
        Assert.Equal("null", node.Rows[2].Cells[2].Text);
        Assert.Equal("Result, table, 3 rows, 3 columns", node.AccessibleName);
    }

    [Theory]
    [InlineData("""{ "a": [1, 2], "b": [1, 2, 3] }""")]
    [InlineData("""{ "a": [1, 2], "b": "two" }""")]
    [InlineData("""{ "a": [1, 2], "b": [[1], [2]] }""")]
    [InlineData("""{ "a": [{ "x": 1 }, { "x": 2 }], "b": [1, 2] }""")]
    [InlineData("""{ "a": [], "b": [] }""")]
    public void Anything_else_keeps_its_shape(string json)
    {
        Assert.Equal(ResultShape.Properties, Build(json).Shape);
    }

    [Fact]
    public void One_list_alone_is_a_list_and_not_a_one_column_table()
    {
        // Unwrapped as a wrapper of one, which is the rule that already applied to it.
        var node = Build("""{ "elevation": [87] }""");

        Assert.Equal(ResultShape.List, node.Shape);
        Assert.Equal("Elevation", node.Label);
    }

    [Fact]
    public void A_column_table_takes_its_column_names_from_the_schema()
    {
        var node = Build(
            """{ "t": [1, 2], "v": [3, 4] }""",
            """{ "type": "object", "properties": { "v": { "title": "Value", "description": "What was measured." } } }""");

        Assert.Equal("Value", node.Columns[1].Label);
        Assert.Equal("What was measured.", node.Columns[1].HelpText);
    }

    [Fact]
    public void A_column_table_bigger_than_the_limit_reports_its_real_size()
    {
        var times = string.Join(",", Enumerable.Range(0, 500));
        var node = Build($$"""{ "t": [{{times}}], "v": [{{times}}] }""");

        Assert.Equal(500, node.TotalRows);
        Assert.Equal(ResultBuildOptions.Default.MaxRows, node.Rows.Count);
        Assert.True(node.IsTruncated);
    }

    [Fact]
    public void A_real_forecast_is_a_tree_with_its_hours_and_days_as_tables()
    {
        Assert.True(JsonText.TryRead(ResultFixtures.Text("open-meteo", "weather_forecast"), out _, out var value));
        var node = ResultBuilder.Build(null, value);

        Assert.Equal(ResultShape.Properties, node.Shape);

        var hourly = Assert.Single(node.Children, c => c.Label == "Hourly");
        Assert.Equal("Hourly, table, 48 rows, 3 columns", hourly.AccessibleName);
        Assert.Equal(["Time", "Temperature 2 m", "Precipitation"], hourly.Columns.Select(c => c.Label));
        Assert.Equal(["2026-10-19T14:00", "18.9", "0"], hourly.Rows[14].Cells.Select(c => c.Text));

        var daily = Assert.Single(node.Children, c => c.Label == "Daily");
        Assert.Equal("Daily, table, 2 rows, 3 columns", daily.AccessibleName);

        // The units are an object of strings beside it, and stay one: joining them onto the
        // columns would be a rule for one server.
        Assert.Equal(ResultShape.Properties, Assert.Single(node.Children, c => c.Label == "Hourly units").Shape);
    }

    [Fact]
    public void Real_geocoding_results_are_a_table()
    {
        Assert.True(JsonText.TryRead(ResultFixtures.Text("open-meteo", "geocoding"), out _, out var value));
        var node = ResultBuilder.Build(null, value);

        var table = node.Shape == ResultShape.Table ? node : Assert.Single(node.Children, c => c.Shape == ResultShape.Table);

        Assert.Equal("Results", table.Label);
        Assert.Contains(table.Columns, c => c.Label == "Population");
        Assert.Equal("Toronto", table.Rows[0].Cells[table.Columns.ToList().FindIndex(c => c.Name == "name")].Text);
    }

    // ------------------------------------------------------------------ the wrapper of one

    [Fact]
    public void A_wrapper_of_one_property_is_taken_off_and_its_name_kept()
    {
        // SEP-2106's object requirement, not the server's structure: 17 of the corpus's 25 output
        // schemas are one property holding the answer, 13 of them a bare string.
        var node = Build("""{ "entities": [ { "name": "Ada" } ] }""");

        Assert.Equal(ResultShape.Table, node.Shape);
        Assert.Equal("Entities", node.Label);
        Assert.True(node.IsRoot);
    }

    [Fact]
    public void The_schemas_title_survives_the_unwrapping()
    {
        var node = Build(
            """{ "rows": [ { "a": 1 } ] }""",
            """
            {
              "type": "object",
              "properties": { "rows": { "title": "Matching rows", "type": "array" } }
            }
            """);

        Assert.Equal("Matching rows", node.Label);
    }

    [Fact]
    public void An_object_with_more_than_one_property_keeps_its_shape()
    {
        var node = Build("""{ "a": 1, "b": 2 }""");

        Assert.Equal(ResultShape.Properties, node.Shape);
        Assert.Equal("Result", node.Label);
    }

    // ------------------------------------------------------------------ the corpus

    [Theory]
    [MemberData(nameof(OutputSchemas))]
    public void Every_published_output_schema_renders_a_result(string key)
    {
        // The corpus has schemas but no answers, so the answer is a skeleton built from the schema
        // itself — which is the same trick Tier 3 uses to seed its editor, and it means these are
        // real published shapes rather than shapes we invented (ROADMAP §9).
        var entry = Corpus.Get(key);
        var schema = entry.OutputSchema!.Value;
        var skeleton = JsonSkeleton.For(schema);

        var value = skeleton.Length > 0 ? JsonDocument.Parse(skeleton).RootElement : default;
        var node = ResultBuilder.Build(schema, value, "Structured result");

        Assert.NotEqual("", node.AccessibleName);
        Assert.All(node.SelfAndDescendants(), n => Assert.NotEqual("", n.AccessibleName));
    }

    [Fact]
    public void The_corpus_says_what_a_published_output_schema_looks_like()
    {
        var described = Corpus.Entries.Where(e => e.OutputSchema is not null).ToList();

        var properties = described
            .Select(e => e.OutputSchema!.Value)
            .Select(schema => schema.TryGetProperty("properties", out var members)
                ? members.EnumerateObject().ToList()
                : [])
            .ToList();

        var wrappers = properties.Count(p => p.Count == 1);
        var stringWrappers = properties.Count(p =>
            p.Count == 1
            && p[0].Value.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "string");

        // Three measurements, and every one of them decided something. That 25 of 213 tools publish
        // a schema at all is why the renderer takes one optionally (§6.1, hedge 5). That 17 of the
        // 25 are a wrapper of one property is why the wrapper comes off (ResultBuilder.BuildRoot).
        // That 13 of those hold a bare string — which the tool has already sent as text — is why
        // RepeatsText exists at all.
        Assert.Equal(213, Corpus.Entries.Count);
        Assert.Equal(25, described.Count);
        Assert.Equal(17, wrappers);
        Assert.Equal(13, stringWrappers);
    }

    public static TheoryData<string> OutputSchemas()
    {
        var data = new TheoryData<string>();

        foreach (var entry in Corpus.Entries.Where(e => e.OutputSchema is not null))
            data.Add(entry.Key);

        return data;
    }
}
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humcp.Schema.Tests;

/// <summary>
/// Validation is advice, and the advice is spoken (ROADMAP §5, §7.5). These tests are as much about
/// the sentences as about the verdicts.
/// </summary>
public class SchemaValidatorTests
{
    private const string Person = """
    {
      "type": "object",
      "required": ["name", "email"],
      "properties": {
        "name":   {"type": "string", "minLength": 2, "maxLength": 40},
        "email":  {"type": "string", "format": "email"},
        "age":    {"type": "integer", "minimum": 0, "maximum": 130},
        "role":   {"type": "string", "enum": ["admin", "reader"]},
        "tags":   {"type": "array", "items": {"type": "string"}, "maxItems": 3}
      }
    }
    """;

    [Fact]
    public void A_good_value_produces_nothing_to_say()
    {
        var report = Check(Person, """{"name": "Ada", "email": "ada@example.com"}""");

        Assert.True(report.IsValid);
        Assert.True(report.WasChecked);
        Assert.Equal("No problems found.", report.Summary);
    }

    [Fact]
    public void A_missing_required_field_is_reported_against_that_field_and_not_its_parent()
    {
        // §7.8 moves focus to the first invalid field. An error whose path is the whole object has
        // nowhere to send it.
        var report = Check(Person, """{"name": "Ada"}""");

        var issue = Assert.Single(report.Issues);
        Assert.Equal("email", issue.FieldPath);
        Assert.Equal("required", issue.Keyword);
        Assert.Equal("Email: This one is required.", issue.Announcement);
        Assert.Equal("email", report.FirstInvalidPath);
    }

    [Theory]
    [InlineData("""{"name": "A", "email": "a@b.co"}""", "name", "This needs at least 2 characters.")]
    [InlineData("""{"name": "Ada", "email": "nope"}""", "email", "This should be an email address.")]
    [InlineData("""{"name": "Ada", "email": "a@b.co", "age": 999}""", "age", "This has to be 130 or less.")]
    [InlineData("""{"name": "Ada", "email": "a@b.co", "age": -1}""", "age", "This has to be 0 or more.")]
    [InlineData("""{"name": "Ada", "email": "a@b.co", "age": "old"}""", "age", "This should be a whole number.")]
    [InlineData("""{"name": "Ada", "email": "a@b.co", "role": "owner"}""", "role", "This has to be one of: admin or reader.")]
    [InlineData("""{"name": "Ada", "email": "a@b.co", "tags": ["a","b","c","d"]}""", "tags", "This can hold at most 3 items.")]
    public void Each_kind_of_problem_is_a_sentence_a_person_could_hear(string json, string path, string expected)
    {
        var issue = Assert.Single(Check(Person, json).Issues);

        Assert.Equal(path, issue.FieldPath);
        Assert.Equal(expected, issue.Message);
    }

    [Fact]
    public void A_problem_inside_a_list_names_the_entry_it_is_in()
    {
        var report = Check(Person, """{"name": "Ada", "email": "a@b.co", "tags": ["ok", 7]}""");

        var issue = Assert.Single(report.Issues);

        // Two paths on purpose: the form needs one that matches a control, the summary needs one
        // that tells the user which row to go to.
        Assert.Equal("tags[]", issue.FieldPath);
        Assert.Equal("tags[1]", issue.InstancePath);

        // Not "Item", which is what every row of every list is called.
        Assert.Equal("Tags, entry 2: This should be text.", issue.Announcement);
    }

    [Fact]
    public void The_shallowest_problems_are_reported_first()
    {
        var schema = """
        {"type": "object", "required": ["top"],
         "properties": {"nested": {"type": "object", "properties": {"deep": {"type": "integer"}}}}}
        """;

        var report = Check(schema, """{"nested": {"deep": "no"}}""");

        Assert.Equal(2, report.Issues.Count);
        Assert.Equal("top", report.Issues[0].FieldPath);
        Assert.Equal("nested.deep", report.Issues[1].FieldPath);
    }

    [Fact]
    public void One_problem_deep_in_a_form_is_reported_once()
    {
        // Without suppressing applicator errors the same failure surfaces again at every object
        // above it, and the user hears it three times with only the innermost naming the field.
        var schema = """
        {"type": "object", "properties":
          {"a": {"type": "object", "properties":
            {"b": {"type": "object", "properties": {"c": {"type": "integer"}}}}}}}
        """;

        var report = Check(schema, """{"a": {"b": {"c": "no"}}}""");

        var issue = Assert.Single(report.Issues);
        Assert.Equal("a.b.c", issue.FieldPath);
    }

    [Fact]
    public void The_summary_is_a_count_once_there_is_more_than_one()
    {
        var report = Check(Person, """{"age": 999}""");

        Assert.Equal(3, report.Issues.Count);
        Assert.Equal("3 problems found.", report.Summary);
    }

    [Fact]
    public void Labels_come_from_the_form_where_the_form_knows_them()
    {
        var form = FormBuilder.Build(Parse("""
        {"type": "object", "required": ["repo_path"],
         "properties": {"repo_path": {"type": "string", "title": "Repository path"}}}
        """));

        var issue = Assert.Single(SchemaValidator.Validate(form, new JsonObject()).Issues);

        Assert.Equal("Repository path: This one is required.", issue.Announcement);
    }

    // ------------------------------------------------------------------ never a veto

    [Fact]
    public void A_schema_the_validator_cannot_load_reports_unchecked_rather_than_invalid()
    {
        // ROADMAP §5: the server is the authority. A schema we cannot evaluate is a fact about the
        // server, and it must not become a reason to stop the user calling the tool.
        var report = Check("""{"type": "object", "properties": {"a": {"$ref": "https://example.com/x.json"}}}""",
            """{"a": 1}""");

        Assert.False(report.WasChecked);
        Assert.True(report.IsValid);
        Assert.Contains("could not be checked", report.Summary);
    }

    [Fact]
    public void Evaluation_never_reaches_the_network()
    {
        // The registry's fetch hook is pinned shut, so a $ref chosen by a third-party server cannot
        // turn opening a form into an outbound request. If this ever starts passing by *fetching*,
        // it will hang rather than fail, which is the tell.
        var report = Check("""{"$ref": "http://127.0.0.1:1/never.json"}""", "1");

        Assert.False(report.WasChecked);
    }

    [Fact]
    public void Invalid_json_in_the_raw_editor_is_one_plain_sentence()
    {
        var report = SchemaValidator.ValidateJson(Parse(Person), "{ this is not json");

        var issue = Assert.Single(report.Issues);
        Assert.Equal("json", issue.Keyword);
        Assert.StartsWith("This is not valid JSON.", issue.Message);
    }

    [Fact]
    public void The_raw_editor_forgives_trailing_commas_and_comments()
    {
        // The Tier 3 editor is where a person types JSON by hand, at which point being strict about
        // a trailing comma is picking a fight that helps nobody.
        var report = SchemaValidator.ValidateJson(Parse(Person), """
        {
          "name": "Ada",       // the first
          "email": "ada@example.com",
        }
        """);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void There_is_a_limit_on_how_much_is_reported_at_once()
    {
        var properties = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"p{i}\": {{\"type\": \"integer\"}}"));
        var values = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"p{i}\": \"no\""));

        var report = Check(
            """{"type": "object", "properties": {""" + properties + "}}",
            "{" + values + "}");

        Assert.Equal(SchemaValidator.MaxIssues, report.Issues.Count);
    }

    [Fact]
    public void An_untyped_schema_is_unchecked_rather_than_wrong()
    {
        var report = SchemaValidator.Validate(default(JsonElement), JsonNode.Parse("1"));

        Assert.False(report.WasChecked);
        Assert.True(report.IsValid);
    }

    private static ValidationReport Check(string schema, string value) =>
        SchemaValidator.Validate(Parse(schema), JsonNode.Parse(value));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}

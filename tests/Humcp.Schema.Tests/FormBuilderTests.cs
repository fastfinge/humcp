using System.Text.Json;

namespace Humcp.Schema.Tests;

/// <summary>
/// The tier rules of ROADMAP §5, one at a time, on schemas small enough to read. The corpus proves
/// the engine survives the real world; these say what it is supposed to do.
/// </summary>
public class FormBuilderTests
{
    // ------------------------------------------------------------------ Tier 1

    [Theory]
    [InlineData("""{"type": "string"}""", FieldKind.Text)]
    [InlineData("""{"type": "integer"}""", FieldKind.Integer)]
    [InlineData("""{"type": "number"}""", FieldKind.Number)]
    [InlineData("""{"type": "boolean"}""", FieldKind.Boolean)]
    [InlineData("""{"type": "string", "format": "date"}""", FieldKind.Date)]
    [InlineData("""{"type": "string", "format": "date-time"}""", FieldKind.DateTime)]
    [InlineData("""{"type": "string", "maxLength": 5000}""", FieldKind.MultilineText)]
    [InlineData("""{"type": "string", "enum": ["a", "b"]}""", FieldKind.Choice)]
    [InlineData("""{"type": "array", "items": {"type": "string"}}""", FieldKind.PrimitiveList)]
    public void A_primitive_gets_its_own_control(string json, FieldKind expected)
    {
        var field = OneField(json);

        Assert.Equal(expected, field.Kind);
        Assert.Equal(FieldTier.Direct, field.Tier);
    }

    [Fact]
    public void The_elicitation_subset_renders_entirely_at_tier_one()
    {
        // ROADMAP §3: elicitation's requestedSchema is deliberately restricted to flat objects of
        // primitives, and Tier 1 is a strict superset of it. If this ever fails, Phase 5's forms
        // stop being free.
        var form = Build("""
        {
          "type": "object",
          "properties": {
            "name":     {"type": "string", "minLength": 1, "maxLength": 40},
            "age":      {"type": "integer", "minimum": 0, "maximum": 130},
            "height":   {"type": "number"},
            "agree":    {"type": "boolean"},
            "email":    {"type": "string", "format": "email"},
            "site":     {"type": "string", "format": "uri"},
            "born":     {"type": "string", "format": "date"},
            "seen":     {"type": "string", "format": "date-time"},
            "colour":   {"type": "string", "enum": ["red", "green"], "enumNames": ["Red", "Green"]}
          },
          "required": ["name"]
        }
        """);

        Assert.Equal(FieldTier.Direct, form.HighestTier);
        Assert.Equal(9, form.Fields.Count);
        Assert.Equal(1, form.RequiredCount);
    }

    [Fact]
    public void EnumNames_supply_the_labels_and_enum_supplies_the_values()
    {
        var field = OneField("""{"type": "string", "enum": ["gh", "gl"], "enumNames": ["GitHub", "GitLab"]}""");

        Assert.Equal(["GitHub", "GitLab"], field.Choices.Select(c => c.Label));
        Assert.Equal(["gh", "gl"], field.Choices.Select(c => c.ValueText));
    }

    [Fact]
    public void An_enum_value_with_no_label_is_left_exactly_as_it_is()
    {
        // Humanising would make "us-east-1" into "Us east 1" — a value nobody could type back and
        // nothing the server would accept. Identifiers are read out as identifiers.
        var field = OneField("""{"type": "string", "enum": ["us-east-1", "PULL_REQUEST"]}""");

        Assert.Equal(["us-east-1", "PULL_REQUEST"], field.Choices.Select(c => c.Label));
    }

    [Fact]
    public void A_nullable_type_array_stays_a_single_control()
    {
        var field = OneField("""{"type": ["string", "null"]}""");

        Assert.Equal(FieldKind.Text, field.Kind);
        Assert.Equal(FieldTier.Direct, field.Tier);
        Assert.True(field.AllowsNull);
    }

    [Fact]
    public void A_nullable_union_is_one_field_and_not_a_json_editor()
    {
        // anyOf: [X, null] is how most generators spell "optional X". Treating it as a union would
        // send a large fraction of real parameters to Tier 3 for no reason at all.
        var field = OneField("""{"anyOf": [{"type": "integer", "minimum": 1}, {"type": "null"}]}""");

        Assert.Equal(FieldKind.Integer, field.Kind);
        Assert.Equal(FieldTier.Direct, field.Tier);
        Assert.True(field.AllowsNull);
        Assert.Equal(1, field.Constraints.Minimum);
    }

    [Fact]
    public void A_union_of_constants_is_a_choice()
    {
        var field = OneField("""
        {"oneOf": [
            {"const": "asc",  "title": "Ascending"},
            {"const": "desc", "title": "Descending"}
        ]}
        """);

        Assert.Equal(FieldKind.Choice, field.Kind);
        Assert.Equal(FieldTier.Direct, field.Tier);
        Assert.Equal(["Ascending", "Descending"], field.Choices.Select(c => c.Label));
        Assert.Equal(["asc", "desc"], field.Choices.Select(c => c.ValueText));
    }

    [Fact]
    public void A_type_is_inferred_when_the_schema_forgets_to_state_one()
    {
        Assert.Equal(FieldKind.Text, OneField("""{"maxLength": 8}""").Kind);
        Assert.Equal(FieldKind.Number, OneField("""{"minimum": 0}""").Kind);
        Assert.Equal(FieldKind.Group, OneField("""{"properties": {"a": {"type": "string"}}}""").Kind);
    }

    // ------------------------------------------------------------------ Tier 2

    [Fact]
    public void An_object_becomes_a_group_with_its_own_fields()
    {
        var field = OneField("""
        {"type": "object", "properties": {"host": {"type": "string"}, "port": {"type": "integer"}},
         "required": ["host"]}
        """);

        Assert.Equal(FieldKind.Group, field.Kind);
        Assert.Equal(FieldTier.Composite, field.Tier);
        Assert.Equal(["value.host", "value.port"], field.Fields.Select(f => f.Path));
        Assert.True(field.Fields[0].IsRequired);
        Assert.False(field.Fields[1].IsRequired);
    }

    [Fact]
    public void An_array_of_objects_becomes_a_list_of_sub_forms()
    {
        var field = OneField("""
        {"type": "array", "items": {"type": "object", "properties": {"name": {"type": "string"}}}}
        """);

        Assert.Equal(FieldKind.ObjectList, field.Kind);
        Assert.Equal(FieldTier.Composite, field.Tier);
        Assert.Equal(["value[].name"], field.Fields.Select(f => f.Path));
    }

    [Fact]
    public void A_ref_is_resolved_against_defs()
    {
        var form = Build("""
        {
          "type": "object",
          "$defs": {"port": {"type": "integer", "minimum": 1, "maximum": 65535}},
          "properties": {"listen": {"$ref": "#/$defs/port"}}
        }
        """);

        var field = form.Fields.Single();

        Assert.Equal(FieldKind.Integer, field.Kind);
        Assert.Equal(FieldTier.Direct, field.Tier);
        Assert.Equal(65535, field.Constraints.Maximum);
    }

    [Fact]
    public void A_local_description_beside_a_ref_wins_over_the_one_it_points_at()
    {
        // 2020-12 allows keywords beside $ref. When both say something, the one written at the use
        // site is the one about this field.
        var form = Build("""
        {
          "type": "object",
          "$defs": {"id": {"type": "string", "description": "An identifier."}},
          "properties": {"owner": {"$ref": "#/$defs/id", "description": "Who owns the repository."}}
        }
        """);

        Assert.Equal("Who owns the repository.", form.Fields.Single().HelpText);
    }

    [Fact]
    public void An_allOf_of_object_schemas_is_merged_into_one_group()
    {
        var field = OneField("""
        {"allOf": [
            {"type": "object", "properties": {"a": {"type": "string"}}, "required": ["a"]},
            {"type": "object", "properties": {"b": {"type": "boolean"}}}
        ]}
        """);

        Assert.Equal(FieldKind.Group, field.Kind);
        Assert.Equal(["a", "b"], field.Fields.Select(f => f.Name));
        Assert.True(field.Fields[0].IsRequired);
    }

    // ------------------------------------------------------------------ Tier 3

    [Theory]
    [InlineData("""{"oneOf": [{"type": "string"}, {"type": "integer"}]}""")]
    [InlineData("""{"anyOf": [{"type": "object", "properties": {"a": {}}}, {"type": "string"}]}""")]
    [InlineData("""{"if": {"const": "x"}, "then": {"type": "string"}}""")]
    [InlineData("""{"not": {"type": "string"}}""")]
    [InlineData("""{"type": "object", "additionalProperties": true}""")]
    [InlineData("""{"type": "array"}""")]
    [InlineData("""{"type": "array", "prefixItems": [{"type": "string"}, {"type": "integer"}]}""")]
    public void What_cannot_be_typed_becomes_a_json_editor_that_says_why(string json)
    {
        var field = OneField(json);

        Assert.Equal(FieldKind.Raw, field.Kind);
        Assert.Equal(FieldTier.Raw, field.Tier);
        Assert.False(string.IsNullOrWhiteSpace(field.DegradedReason));

        // §7.18: at an edge the app says what it cannot do. A Tier 3 box with no explanation is
        // indistinguishable from a bug.
        Assert.Contains(field.DegradedReason!, field.HelpText);
    }

    [Fact]
    public void A_recursive_ref_demotes_to_tier_three_rather_than_recursing()
    {
        var form = Build("""
        {
          "type": "object",
          "$defs": {
            "node": {
              "type": "object",
              "properties": {"name": {"type": "string"}, "child": {"$ref": "#/$defs/node"}}
            }
          },
          "properties": {"tree": {"$ref": "#/$defs/node"}}
        }
        """);

        var tree = form.Fields.Single();
        Assert.Equal(FieldKind.Group, tree.Kind);

        var child = tree.Fields.Single(f => f.Name == "child");
        Assert.Equal(FieldTier.Raw, child.Tier);
        Assert.Contains("itself", child.DegradedReason);
    }

    [Fact]
    public void The_same_definition_used_twice_is_not_a_cycle()
    {
        // Cycle detection has to be along one branch, not across the document. Two siblings
        // pointing at one $def is the ordinary case, and demoting it would be a bad trade.
        var form = Build("""
        {
          "type": "object",
          "$defs": {"name": {"type": "string", "maxLength": 40}},
          "properties": {"first": {"$ref": "#/$defs/name"}, "last": {"$ref": "#/$defs/name"}}
        }
        """);

        Assert.All(form.Fields, f => Assert.Equal(FieldTier.Direct, f.Tier));
    }

    [Fact]
    public void An_external_ref_is_reported_rather_than_fetched()
    {
        var field = OneField("""{"$ref": "https://example.com/person.json"}""");

        Assert.Equal(FieldTier.Raw, field.Tier);
        Assert.Contains("external reference", field.DegradedReason);
    }

    [Fact]
    public void Nesting_past_the_limit_degrades_instead_of_going_forever()
    {
        var schema = Nested(depth: 30);
        var form = FormBuilder.Build(Parse(schema), new FormBuildOptions { MaxDepth = 4 });

        var deepest = form.AllFields().Last();
        Assert.Equal(FieldTier.Raw, deepest.Tier);
        Assert.Contains("deeper", deepest.DegradedReason);
    }

    // ------------------------------------------------------------------ the whole form

    [Fact]
    public void A_tool_that_takes_no_arguments_is_an_empty_form_and_not_a_degraded_one()
    {
        // Both spellings a real server uses. Neither is a failure to render: it is a form with
        // nothing on it, which is what a zero-argument tool needs.
        foreach (var json in new[] { """{"type": "object"}""", """{"type": "object", "properties": {}}""" })
        {
            var form = Build(json);

            Assert.True(form.IsEmpty);
            Assert.False(form.IsWhollyRaw);
            Assert.Null(form.DegradedReason);
        }
    }

    [Fact]
    public void An_empty_schema_is_taken_at_its_word()
    {
        // Not invented: this is verbatim what every tool of @modelcontextprotocol/server-gitlab
        // publishes as its inputSchema — a dialect declaration and nothing else, from a
        // zod-to-json-schema conversion that lost the body. The schema says the tool takes no
        // arguments, so that is what we render. It is wrong, and it is wrong in the one way we
        // cannot do anything about: no client can recover parameters a server never published.
        var form = Build("""{"$schema": "http://json-schema.org/draft-07/schema#"}""");

        Assert.True(form.IsEmpty);
        Assert.False(form.IsWhollyRaw);
    }

    [Fact]
    public void A_schema_that_is_not_an_object_at_the_root_becomes_one_editor_for_the_lot()
    {
        var form = Build("""{"type": "array", "items": {"type": "string"}}""");

        Assert.True(form.IsWhollyRaw);
        Assert.Equal(FieldKind.Raw, form.Fields.Single().Kind);
        Assert.Equal("Arguments", form.Fields.Single().Label);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"nonsense\"")]
    [InlineData("[]")]
    public void Nothing_defeats_it(string json)
    {
        var form = FormBuilder.Build(Parse(json));

        Assert.NotNull(form);
        Assert.All(form.AllFields(), f => Assert.NotEqual("", f.Label));
    }

    [Fact]
    public void An_undefined_element_is_a_form_and_not_an_exception()
    {
        var form = FormBuilder.Build(default);

        Assert.True(form.IsWhollyRaw);
    }

    [Fact]
    public void A_field_survives_the_document_it_arrived_in_being_disposed()
    {
        // ToolInfo.InputSchema points into a document the MCP client owns, and a FieldModel outlives
        // the tool listing that produced it. Building has to detach.
        FormModel form;

        using (var document = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"}}}"""))
        {
            form = FormBuilder.Build(document.RootElement);
        }

        Assert.Equal("string", form.Fields.Single().Schema.GetProperty("type").GetString());
    }

    [Fact]
    public void Hostile_text_never_reaches_a_label_or_help_string_intact()
    {
        var bell = char.ConvertFromUtf32(0x0007);
        var override_ = char.ConvertFromUtf32(0x202E);
        var schema = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["a"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["title"] = $"Name{bell}{override_}here",
                    ["description"] = "First sentence. " + new string('x', 10_000),
                },
            },
        });

        var field = Build(schema).Fields.Single();

        // The bell became a space and the bidi override vanished: a control character was standing
        // between two words, and a formatting character was standing between nothing at all.
        Assert.Equal("Name here", field.Label);
        Assert.Equal("First sentence.", field.HelpText);
        Assert.True(field.FullDescription.Length <= TextSanitizer.DetailsLength);
    }

    // ------------------------------------------------------------------ helpers

    private static FormModel Build(string json) => FormBuilder.Build(Parse(json));

    /// <summary>Wraps one schema as the only property of an object, and returns the field for it.</summary>
    private static FieldModel OneField(string json) =>
        Build("""{"type": "object", "properties": {"value": """ + json + "}}").Fields.Single();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string Nested(int depth)
    {
        var json = """{"type": "string"}""";

        for (var i = 0; i < depth; i++)
            json = """{"type": "object", "properties": {"down": """ + json + "}}";

        return json;
    }
}

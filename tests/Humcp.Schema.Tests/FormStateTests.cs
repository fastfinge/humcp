using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humcp.Schema.Tests;

/// <summary>
/// What a form adds up to when someone has finished filling it in (ROADMAP §10, Phase 3). The
/// rule under most of these is the omission one: a value the user did not choose is not sent.
/// </summary>
public class FormStateTests
{
    // ------------------------------------------------------------------ the omission rule

    [Fact]
    public void An_empty_form_sends_an_empty_argument_object()
    {
        var state = State("""
        {"type": "object", "properties": {"a": {"type": "string"}, "b": {"type": "integer"}}}
        """);

        var build = state.Build();

        Assert.True(build.Ok);
        Assert.Empty(build.Arguments!);
    }

    [Fact]
    public void A_default_the_user_never_touched_is_not_repeated_back()
    {
        // The server already knows its own default, and it may have changed it since it wrote the
        // schema. Sending it back pins a value nobody asked to pin.
        var state = State("""
        {"type": "object", "properties": {"format": {"type": "string", "default": "markdown"}}}
        """);

        Assert.Equal("markdown", Text(state, "format").Text);
        Assert.Empty(state.Build().Arguments!);
    }

    [Fact]
    public void A_default_the_user_changed_is_sent()
    {
        var state = State("""
        {"type": "object", "properties": {"format": {"type": "string", "default": "markdown"}}}
        """);

        Text(state, "format").Text = "html";

        Assert.Equal("html", state.Build().Arguments!["format"]!.GetValue<string>());
    }

    [Fact]
    public void A_required_field_is_sent_even_when_it_still_holds_its_default()
    {
        // The other half of the rule: a required parameter has to appear in the call, so whatever
        // is in the box is what the call says.
        var state = State("""
        {"type": "object", "required": ["format"],
         "properties": {"format": {"type": "string", "default": "markdown"}}}
        """);

        Assert.Equal("markdown", state.Build().Arguments!["format"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ text and numbers

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("  42  ", 42L)]
    [InlineData("-7", -7L)]
    public void Whole_numbers_are_sent_as_numbers(string typed, long expected)
    {
        var state = State("""{"type": "object", "properties": {"n": {"type": "integer"}}}""");
        Text(state, "n").Text = typed;

        Assert.Equal(expected, state.Build().Arguments!["n"]!.GetValue<long>());
    }

    [Fact]
    public void A_decimal_point_is_read_the_way_the_user_types_it_and_the_way_a_document_spells_it()
    {
        var state = State("""{"type": "object", "properties": {"n": {"type": "number"}}}""");

        Text(state, "n").Text = 1.5m.ToString(CultureInfo.CurrentCulture);
        Assert.Equal(1.5m, state.Build().Arguments!["n"]!.GetValue<decimal>());

        Text(state, "n").Text = "2.5";
        Assert.Equal(2.5m, state.Build().Arguments!["n"]!.GetValue<decimal>());
    }

    [Fact]
    public void Text_that_is_not_a_number_is_sent_as_text_so_the_validator_can_say_so_in_words()
    {
        // Deliberately not a second error channel. The schema already knows this should be a
        // number, and SchemaValidator already has a sentence for it.
        var state = State("""{"type": "object", "properties": {"n": {"type": "integer"}}}""");
        Text(state, "n").Text = "seven";

        var submission = state.Prepare();

        Assert.True(submission.CanSend);
        Assert.Equal("seven", submission.Arguments["n"]!.GetValue<string>());
        Assert.Equal("This should be a whole number.", Text(state, "n").Status);
    }

    [Fact]
    public void A_required_nullable_field_left_empty_sends_null_rather_than_nothing()
    {
        // §5: an empty box means null rather than an empty string. It only reaches the wire when
        // the call has to say something, which is what "required" means.
        var state = State("""
        {"type": "object", "required": ["note"],
         "properties": {"note": {"type": ["string", "null"]}}}
        """);

        var arguments = state.Build().Arguments!;

        Assert.True(arguments.ContainsKey("note"));
        Assert.Null(arguments["note"]);
    }

    // ------------------------------------------------------------------ choices

    [Fact]
    public void A_choice_always_offers_a_way_back_to_unset()
    {
        var state = State("""
        {"type": "object", "required": ["order"],
         "properties": {"order": {"type": "string", "enum": ["asc", "desc"]}}}
        """);

        var choice = (ChoiceFieldState)state.Fields.Single();

        Assert.Same(ChoiceFieldState.NotSet, choice.Options[0]);
        Assert.Equal(3, choice.Options.Count);
        Assert.Same(ChoiceFieldState.NotSet, choice.Selected);
        Assert.False(choice.IsSet);
    }

    [Fact]
    public void A_chosen_option_is_sent_in_the_json_type_the_schema_used()
    {
        var state = State("""
        {"type": "object", "properties": {"limit": {"enum": [10, 20, 30]}}}
        """);

        var choice = (ChoiceFieldState)state.Fields.Single();
        choice.Selected = choice.Options.Last();

        Assert.Equal(30, state.Build().Arguments!["limit"]!.GetValue<int>());
    }

    [Fact]
    public void A_default_selects_its_option_without_sending_it()
    {
        var state = State("""
        {"type": "object", "properties": {"order": {"type": "string", "enum": ["asc", "desc"], "default": "desc"}}}
        """);

        var choice = (ChoiceFieldState)state.Fields.Single();

        Assert.Equal("desc", choice.Selected!.ValueText);
        Assert.Empty(state.Build().Arguments!);
    }

    // ------------------------------------------------------------------ booleans

    [Fact]
    public void An_untouched_optional_checkbox_says_nothing()
    {
        // 91 of the corpus's 134 booleans are optional and state no default. For those, "unticked"
        // is a statement nobody made.
        var state = State("""{"type": "object", "properties": {"force": {"type": "boolean"}}}""");

        Assert.False(state.Fields.Single().IsSet);
        Assert.Empty(state.Build().Arguments!);
    }

    [Fact]
    public void A_ticked_checkbox_sends_true()
    {
        var state = State("""{"type": "object", "properties": {"force": {"type": "boolean"}}}""");
        ((BooleanFieldState)state.Fields.Single()).Value = true;

        Assert.True(state.Build().Arguments!["force"]!.GetValue<bool>());
    }

    [Fact]
    public void A_default_true_checkbox_can_be_turned_off()
    {
        // The one case the omission rule cannot cover on its own: leaving it out would let the
        // server's own default back in, so unticking has to say false out loud.
        var state = State("""
        {"type": "object", "properties": {"main": {"type": "boolean", "default": true}}}
        """);

        var flag = (BooleanFieldState)state.Fields.Single();
        Assert.True(flag.Value);
        Assert.Empty(state.Build().Arguments!);

        flag.Value = false;
        Assert.False(state.Build().Arguments!["main"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ lists

    [Fact]
    public void A_list_collects_what_was_added_to_it()
    {
        var state = State("""
        {"type": "object", "properties": {"tags": {"type": "array", "items": {"type": "string"}}}}
        """);

        var list = (ListFieldState)state.Fields.Single();

        Assert.False(list.CanAdd);
        ((TextFieldState)list.Draft).Text = "alpha";
        Assert.True(list.CanAdd);
        Assert.True(list.AddDraft());

        ((TextFieldState)list.Draft).Text = "beta";
        list.AddDraft();

        // The entry box empties itself, so the next value is typed into a box that says nothing.
        Assert.Equal("", ((TextFieldState)list.Draft).Text);
        Assert.Equal(2, list.Count);

        var array = (JsonArray)state.Build().Arguments!["tags"]!;
        Assert.Equal(["alpha", "beta"], array.Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void A_list_seeded_from_a_default_is_not_sent_back_untouched()
    {
        var state = State("""
        {"type": "object",
         "properties": {"tags": {"type": "array", "items": {"type": "string"}, "default": ["a", "b"]}}}
        """);

        var list = (ListFieldState)state.Fields.Single();

        Assert.Equal(2, list.Count);
        Assert.Empty(state.Build().Arguments!);

        list.Remove(list.Items[0]);
        Assert.Single((JsonArray)state.Build().Arguments!["tags"]!);
    }

    [Fact]
    public void A_list_of_choices_edits_through_the_same_control_its_entries_use()
    {
        var state = State("""
        {"type": "object",
         "properties": {"levels": {"type": "array", "items": {"type": "string", "enum": ["info", "warn"]}}}}
        """);

        var list = (ListFieldState)state.Fields.Single();
        var draft = Assert.IsType<ChoiceFieldState>(list.Draft);

        draft.Selected = draft.Options[1];
        Assert.True(list.AddDraft());
        Assert.Same(ChoiceFieldState.NotSet, draft.Selected);

        Assert.Equal("info", ((JsonArray)state.Build().Arguments!["levels"]!)[0]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ groups and object lists

    [Fact]
    public void A_group_sends_only_the_members_that_were_filled_in()
    {
        var state = State("""
        {"type": "object", "properties": {
          "filter": {"type": "object", "properties": {
            "contains": {"type": "string"},
            "limit": {"type": "integer"}}}}}
        """);

        var group = (GroupFieldState)state.Fields.Single();
        Assert.False(group.IsSet);
        Assert.Empty(state.Build().Arguments!);

        ((TextFieldState)group.Fields[0]).Text = "error";

        var filter = (JsonObject)state.Build().Arguments!["filter"]!;
        Assert.Equal("error", filter["contains"]!.GetValue<string>());
        Assert.False(filter.ContainsKey("limit"));
    }

    [Fact]
    public void An_object_list_makes_one_object_per_row()
    {
        var state = State("""
        {"type": "object", "properties": {
          "rules": {"type": "array", "items": {"type": "object", "properties": {
            "field": {"type": "string"}, "value": {"type": "string"}}}}}}
        """);

        var rows = (ObjectListFieldState)state.Fields.Single();
        Assert.Equal("Empty", rows.Display);

        var first = rows.AddRow();
        ((TextFieldState)first.Fields[0]).Text = "status";
        ((TextFieldState)first.Fields[1]).Text = "open";

        rows.AddRow();

        var array = (JsonArray)state.Build().Arguments!["rules"]!;
        Assert.Equal(2, array.Count);
        Assert.Equal("status", array[0]!["field"]!.GetValue<string>());
        Assert.Empty((JsonObject)array[1]!);
        Assert.Equal("2 entries", rows.Display);
    }

    // ------------------------------------------------------------------ Tier 3

    [Fact]
    public void A_raw_editor_starts_with_the_shape_the_schema_describes()
    {
        var state = State("""
        {"type": "object", "properties": {
          "target": {"oneOf": [
            {"type": "object", "properties": {"id": {"type": "integer"}, "name": {"type": "string"}}},
            {"type": "string"}]}}}
        """);

        var raw = Text(state, "target");

        Assert.Equal(FieldTier.Raw, raw.Field.Tier);
        Assert.Contains("\"id\": 0", raw.Text);
        Assert.Contains("\"name\": \"\"", raw.Text);

        // Seeded, and therefore untouched, and therefore not sent.
        Assert.False(raw.IsSet);
    }

    [Fact]
    public void A_raw_editor_sends_what_was_typed_into_it_as_json()
    {
        var state = State("""
        {"type": "object", "properties": {"target": {"oneOf": [{"type": "string"}, {"type": "integer"}]}}}
        """);

        Text(state, "target").Text = """{"id": 3}""";

        Assert.Equal(3, state.Build().Arguments!["target"]!["id"]!.GetValue<int>());
    }

    [Fact]
    public void Text_that_is_not_json_is_the_one_thing_that_stops_a_call()
    {
        // ROADMAP §5 forbids blocking on a validation failure. This is not one: there is no request
        // to send, and §7.18 says an edge has to explain itself.
        var state = State("""
        {"type": "object", "properties": {"target": {"oneOf": [{"type": "string"}, {"type": "integer"}]}}}
        """);

        Text(state, "target").Text = "{not json";

        var submission = state.Prepare();

        Assert.False(submission.CanSend);
        var problem = Assert.Single(submission.Build.Problems);
        Assert.Equal("target", problem.FieldPath);
        Assert.StartsWith("This is not valid JSON, so the call was not sent.", problem.Message);
        Assert.StartsWith("This is not valid JSON", Text(state, "target").Status);
    }

    [Fact]
    public void A_wholly_raw_form_is_one_editor_holding_the_whole_argument_object()
    {
        var state = State("""{"oneOf": [{"type": "object"}, {"type": "string"}]}""");

        Assert.True(state.Form.IsWhollyRaw);

        var editor = Assert.IsType<TextFieldState>(state.Fields.Single());
        editor.Text = """{"a": 1}""";

        var arguments = state.Build().Arguments!;
        Assert.Equal(1, arguments["a"]!.GetValue<int>());
        Assert.False(arguments.ContainsKey(""));
    }

    [Fact]
    public void A_wholly_raw_form_holding_something_other_than_an_object_says_so()
    {
        var state = State("""{"oneOf": [{"type": "object"}, {"type": "string"}]}""");
        ((TextFieldState)state.Fields.Single()).Text = "\"hello\"";

        var problem = Assert.Single(state.Build().Problems);
        Assert.Contains("curly brackets", problem.Message);
    }

    // ------------------------------------------------------------------ validation surfacing

    [Fact]
    public void A_failed_check_leaves_every_field_knowing_what_is_wrong_with_it()
    {
        var state = State("""
        {"type": "object", "required": ["owner", "repo"],
         "properties": {
           "owner": {"type": "string"},
           "repo":  {"type": "string"},
           "count": {"type": "integer", "maximum": 10}}}
        """);

        Text(state, "count").Text = "50";

        var submission = state.Prepare();

        Assert.True(submission.CanSend);
        Assert.Equal(3, submission.Report.Issues.Count);
        Assert.Equal("This one is required.", Text(state, "owner").Status);
        Assert.Equal("This has to be 10 or less.", Text(state, "count").Status);

        // §7.8: focus goes to the first invalid field — first in tab order, which is not the field
        // the report happens to list first. The report sorts alphabetically within a depth, so it
        // would have sent the caret to Count.
        Assert.Equal("count", submission.Report.FirstInvalidPath);
        Assert.Equal("owner", state.FirstProblem!.Path);
    }

    [Fact]
    public void A_fields_problem_is_in_the_text_the_screen_reader_actually_speaks()
    {
        // NVDA does not speak ItemStatus on focus. §7.5 asks for the message on the field, and the
        // field had it, correctly, in a property no reader read out: a manual pass landed on an
        // invalid box and heard the label, the role, "required" and the description, and nothing
        // about what was wrong (phase3-notes 4.9). ItemStatus is still set for the readers that use
        // it; this is what makes the rule true on the one that does not.
        var state = State("""
        {"type": "object", "required": ["owner"],
         "properties": {"owner": {"type": "string", "description": "Who owns it."}}}
        """);

        var owner = Text(state, "owner");
        Assert.Equal("Who owns it.", owner.HelpText);

        state.Prepare();

        Assert.Equal("This one is required.", owner.Status);
        Assert.Equal("Problem: This one is required. Who owns it.", owner.HelpText);

        owner.Text = "ada";
        state.Prepare();

        Assert.Equal("Who owns it.", owner.HelpText);
    }

    [Fact]
    public void Fixing_a_field_and_checking_again_clears_its_status()
    {
        var state = State("""
        {"type": "object", "required": ["owner"], "properties": {"owner": {"type": "string"}}}
        """);

        state.Prepare();
        Assert.NotNull(Text(state, "owner").Status);

        Text(state, "owner").Text = "ada";
        state.Prepare();

        Assert.Null(Text(state, "owner").Status);
    }

    // ------------------------------------------------------------------ the optional toggle

    [Fact]
    public void Optional_parameters_are_off_the_screen_until_they_are_asked_for()
    {
        var state = State("""
        {"type": "object", "required": ["query"],
         "properties": {
           "query": {"type": "string"},
           "limit": {"type": "integer"},
           "deep":  {"type": "boolean"}}}
        """);

        Assert.Equal(3, state.Fields.Count);
        Assert.Equal(["query"], state.VisibleFields.Select(f => f.Path));
        Assert.Equal(2, state.HiddenCount);
        Assert.True(state.HasOptional);

        state.ShowOptional = true;

        Assert.Equal(3, state.VisibleFields.Count);
        Assert.Equal(0, state.HiddenCount);
    }

    [Fact]
    public void A_problem_on_a_hidden_parameter_brings_it_back_onto_the_screen()
    {
        // Otherwise §7.8 has nowhere to send focus, and the user hears about a field that is not
        // there.
        var state = State("""
        {"type": "object", "properties": {"limit": {"type": "integer", "maximum": 10}}}
        """);

        Assert.Empty(state.VisibleFields);
        Text(state, "limit").Text = "50";

        state.Prepare();

        Assert.True(state.ShowOptional);
        Assert.Single(state.VisibleFields);
    }

    [Fact]
    public void Focus_starts_on_the_first_thing_that_has_to_be_filled_in()
    {
        // What is left of §4's enum-first wizard heuristic once the corpus had been asked about it
        // (phase3-notes 3.1).
        var state = State("""
        {"type": "object", "required": ["method"],
         "properties": {
           "context": {"type": "string"},
           "method":  {"type": "string", "enum": ["get", "list"]}}}
        """);

        Assert.Equal("method", state.FirstToFill!.Path);
    }

    // ------------------------------------------------------------------ access keys

    [Fact]
    public void Every_field_gets_a_different_access_key_where_there_are_letters_left()
    {
        var state = State("""
        {"type": "object", "properties": {
          "owner": {"type": "string"},
          "repo":  {"type": "string"},
          "title": {"type": "string"}}}
        """);

        var keys = state.Fields.Select(f => f.AccessKeyIndex is { } i ? char.ToLowerInvariant(f.Label[i]) : '?');

        // Not O, R or T: the window's own chrome has them. Owner falls to N because W is the Window
        // menu, Repo to E because R is the Run button, Title to I because T is the Tool menu.
        Assert.Equal(['n', 'e', 'i'], keys);
    }

    [Theory]
    [InlineData("Tags")]
    [InlineData("Window")]
    [InlineData("Repo")]
    [InlineData("Owner")]
    [InlineData("Close")]
    public void An_access_key_is_never_one_the_windows_own_chrome_took(string label)
    {
        // A menu bar mnemonic beats a control's, so a field that claims T is a field that cannot be
        // reached by its own access key at all — Alt+T opens the Tool menu instead. Found by ear
        // (phase3-notes 4.6); the menu bar arrived after this list was written.
        var assigned = AccessKeys.Assign([label], AccessKeys.WindowCommands);

        var key = assigned[0] is { } index ? char.ToLowerInvariant(label[index]) : '?';

        Assert.DoesNotContain(key, AccessKeys.WindowCommands);
    }

    [Fact]
    public void A_form_that_runs_out_of_letters_leaves_the_rest_without_a_key()
    {
        // A duplicate would be worse: Windows cycles focus between controls sharing a key, and on a
        // generated form that means Alt+S visiting four unrelated boxes.
        var assigned = AccessKeys.Assign(["Ab", "Ab", "Ab"]);

        Assert.Equal(0, assigned[0]);
        Assert.Equal(1, assigned[1]);
        Assert.Null(assigned[2]);
    }

    // ------------------------------------------------------------------ what an items control says

    [Fact]
    public void Anything_bound_into_a_list_says_something_worth_hearing_when_asked_for_its_text()
    {
        // An items control falls back to ToString for an item's accessible name, and the generated
        // one on a record prints every property it has. Three screen readers read a combo box of
        // these as "ChoiceOption { Label = Ascending, Value = …" before this was a test
        // (phase3-notes 4.1). The same trap as ROADMAP §7.12, one layer down.
        var state = State("""
        {"type": "object", "properties": {
          "order": {"type": "string", "enum": ["asc", "desc"], "enumNames": ["Ascending", "Descending"]},
          "tags":  {"type": "array", "items": {"type": "string"}}}}
        """);

        var choice = (ChoiceFieldState)state.Find("order")!;
        Assert.Equal(["Not set", "Ascending", "Descending"], choice.Options.Select(o => o.ToString()));

        var list = (ListFieldState)state.Find("tags")!;
        ((TextFieldState)list.Draft).Text = "urgent";
        list.AddDraft();

        Assert.Equal("urgent", list.Items[0].ToString());
        Assert.DoesNotContain("{", state.Fields.Select(f => f.ToString()!).ToList()[0]);
    }

    // ------------------------------------------------------------------ the corpus

    [Fact]
    public void Every_harvested_schema_produces_a_form_that_can_be_submitted_empty()
    {
        // The Phase 2 invariant carried forward: never fail to render becomes never fail to send.
        // An untouched form is the state every form is in when it opens, so if assembling one can
        // throw, it throws on 213 real tools before anybody types anything.
        var failures = new List<string>();

        foreach (var entry in Corpus.Entries)
        {
            try
            {
                var state = FormState.Create(FormBuilder.Build(entry.InputSchema));
                var submission = state.Prepare();

                if (!submission.CanSend && submission.Build.Problems.Count == 0)
                    failures.Add($"{entry.Key}: no arguments and no reason");
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Key}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Nothing_in_the_corpus_sends_a_parameter_the_user_never_touched()
    {
        // The omission rule over real schemas: 151 corpus fields state a default, and an untouched
        // form must still produce a call that mentions only what the tool insists on. This is the
        // test that found the exception it does not have — an earlier version always sent a
        // default-true checkbox, and five real servers went noisy at once.
        var unexpected = Corpus.Entries
            .Select(e => (e.Key, Form: FormBuilder.Build(e.InputSchema)))
            .Select(x => (x.Key, x.Form, Arguments: FormState.Create(x.Form).Build().Arguments))
            .Where(x => x.Arguments is not null)
            .SelectMany(x => x.Arguments!.Select(p => (x.Key, Name: p.Key,
                Required: x.Form.Fields.Any(f => f.Name == p.Key && f.IsRequired))))
            .Where(x => !x.Required)
            .Select(x => $"{x.Key}.{x.Name}")
            .ToList();

        Assert.Empty(unexpected);
    }

    // ------------------------------------------------------------------ helpers

    [Fact]
    public void The_problem_announced_is_the_one_the_caret_lands_on()
    {
        // Name comes first on the form and Command first in the alphabet. Found by ear: the add-server
        // form said "Command: This one is required" and put the caret in Name (phase6-notes).
        var state = State("""
            { "type": "object",
              "properties": { "name": { "type": "string" }, "command": { "type": "string" } },
              "required": ["name", "command"] }
            """);

        var submission = state.Prepare();

        Assert.Equal("name", state.FirstProblem!.Path);
        Assert.StartsWith("Name:", state.LeadProblem(submission));
    }

    private static FormState State(string schema) =>
        FormState.Create(FormBuilder.Build(JsonDocument.Parse(schema).RootElement));

    private static TextFieldState Text(FormState state, string path) =>
        (TextFieldState)state.Find(path)!;
}

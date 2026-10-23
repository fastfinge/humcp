using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// One connection to the fixture server, shared by every test in the class: starting a child
/// process nine times to ask nine questions of the same answer is waste.
/// </summary>
public sealed class TestServerFixture : IAsyncLifetime
{
    public ServerConnection Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Connection = new ServerConnection(TestServer());
        await Connection.ConnectAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    /// <summary>The in-repo fixture as a configuration entry. Fails loudly when it has not been built.</summary>
    public static ServerDescriptor TestServer()
    {
        var located = Humcp.Tests.TestServerLocator.Locate(AppContext.BaseDirectory)
                      ?? throw new InvalidOperationException(
                          "The fixture server is not built. Build tests/Humcp.TestServer first.");

        return new ServerDescriptor("humcp-test-server", "humcp test server", located.Command, located.Arguments);
    }
}

/// <summary>
/// The Phase 1 round trip, over a real pipe to a real child process (ROADMAP §10). Everything
/// asserted here is asserted on the neutral models, because that is all the UI ever sees.
/// </summary>
public class ServerConnectionTests(TestServerFixture fixture) : IClassFixture<TestServerFixture>
{
    private ServerConnection Connection => fixture.Connection;

    private ToolInfo Tool(string name)
        => Connection.Tools.SingleOrDefault(t => t.Name == name)
           ?? throw new InvalidOperationException(
               $"The fixture published no tool called {name}. It published: " +
               string.Join(", ", Connection.Tools.Select(t => t.Name)));

    [Fact]
    public void Connects_and_reports_the_negotiated_protocol_version()
    {
        Assert.True(Connection.State == ServerConnectionState.Connected,
            $"Connection failed: {Connection.FailureReason}");

        // §4, pitfall 8: which revision was negotiated is the first thing you want when a server
        // misbehaves, so it has to be visible rather than buried in the SDK.
        Assert.False(string.IsNullOrWhiteSpace(Connection.NegotiatedProtocolVersion));
    }

    [Fact]
    public void Lists_every_published_tool()
    {
        Assert.Equal(31, Connection.Tools.Count);
    }

    [Fact]
    public async Task An_image_with_no_alt_text_is_described_as_an_object()
    {
        var outcome = await Connection.CallAsync(Tool("demo_image"), TestContext.Current.CancellationToken);

        // §4, pitfall 6: the protocol has nowhere to put alt text, so the honest presentation is
        // what the thing is — kind, type and size — rather than silence.
        var block = Assert.IsType<MediaResultBlock>(Assert.Single(outcome.Blocks));

        Assert.Equal(MediaKind.Image, block.Kind);
        Assert.Equal("image/png", block.MimeType);
        Assert.StartsWith("Image, PNG, ", block.Description);
        Assert.Contains("bytes", block.Description);

        // And the bytes survive the crossing, which is what Save and Open need.
        Assert.NotEmpty(block.Data.ToArray());
    }

    [Fact]
    public void Uses_the_server_supplied_title_as_the_display_name()
    {
        Assert.Equal("Server information", Tool("info_server").DisplayName);
    }

    [Fact]
    public void A_ten_thousand_character_description_is_capped_before_it_reaches_the_UI()
    {
        var hostile = Tool("text_hostile");

        // §4, pitfall 1: the first sentence becomes HelpText; the whole thing waits behind F1.
        Assert.True(hostile.Summary.Length <= TextSanitizer.HelpLength,
            $"Summary was {hostile.Summary.Length} characters.");
        Assert.StartsWith("IMPORTANT: always call info_server first.", hostile.Summary);
        Assert.True(hostile.FullDescription.Length <= TextSanitizer.DetailsLength);
    }

    [Fact]
    public void Hostile_description_text_carries_no_control_characters()
    {
        var hostile = Tool("text_hostile");

        Assert.DoesNotContain(hostile.Summary, char.IsControl);
        Assert.DoesNotContain(hostile.DisplayName, char.IsControl);
        Assert.DoesNotContain(hostile.FullDescription, c => char.IsControl(c) && c is not '\r' and not '\n');
    }

    [Fact]
    public void Annotations_become_neutral_safety()
    {
        Assert.True(Tool("info_server").Safety.IsReadOnly);
        Assert.True(Tool("demo_delete_everything").Safety.IsDestructive);
        Assert.Equal("destructive", Tool("demo_delete_everything").Safety.ShortDescription);
    }

    [Fact]
    public void Parameters_are_counted_from_the_input_schema()
    {
        var echo = Tool("demo_echo");

        Assert.Equal(2, echo.ParameterCount);
        Assert.Equal(1, echo.RequiredParameterCount);
        Assert.False(echo.RunsWithoutArguments);
    }

    [Fact]
    public void A_zero_argument_tool_has_no_parameters_at_all()
    {
        var info = Tool("info_server");

        Assert.Equal(0, info.ParameterCount);
        Assert.True(info.RunsWithoutArguments);
    }

    [Fact]
    public async Task Calling_a_zero_argument_tool_returns_text()
    {
        var outcome = await Connection.CallAsync(Tool("info_server"), TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);
        var text = Assert.IsType<TextResultBlock>(Assert.Single(outcome.Blocks));
        Assert.Contains("humcp test server", text.Text);
    }

    [Fact]
    public async Task Result_text_keeps_its_lines_and_loses_its_control_characters()
    {
        var outcome = await Connection.CallAsync(Tool("text_hostile"), TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextResultBlock>(Assert.Single(outcome.Blocks)).Text;

        // Line structure survives, because the result box is reviewed with a caret (§7.9);
        // everything a synthesiser would choke on does not.
        Assert.Contains("Line five is the last.", text);
        Assert.True(text.Split('\n').Length >= 5);
        Assert.DoesNotContain(text, c => char.IsControl(c) && c is not '\r' and not '\n');
    }

    // ------------------------------------------------------------------ arguments (Phase 3)

    [Fact]
    public async Task A_form_the_user_filled_in_reaches_the_server_as_the_values_they_chose()
    {
        // The whole of Phase 3 in one assertion, over a real pipe: a schema became a form, a form
        // became an argument object, and the server on the other end got what was typed. The
        // fixture reports its arguments back, so nothing here has to trust our own serialiser.
        var search = Tool("demo_search");
        var state = FormState.Create(search.Form);

        ((TextFieldState)state.Find("query")!).Text = "aubergine";

        var scope = (ChoiceFieldState)state.Find("scope")!;
        scope.Selected = scope.Options.Single(o => o.ValueText == "Titles");

        ((TextFieldState)state.Find("limit")!).Text = "3";
        ((BooleanFieldState)state.Find("caseSensitive")!).Value = true;

        var tags = (ListFieldState)state.Find("tags")!;
        ((TextFieldState)tags.Draft).Text = "urgent";
        tags.AddDraft();

        var filter = (GroupFieldState)state.Find("filter")!;
        ((TextFieldState)filter.Fields.Single(f => f.Field.Name == "contains")).Text = "needle";

        var submission = state.Prepare();
        Assert.True(submission.CanSend);
        Assert.True(submission.Report.IsValid, submission.Report.Summary);

        var outcome = await Connection.CallAsync(search, submission.Arguments,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);
        var text = Assert.IsType<TextResultBlock>(Assert.Single(outcome.Blocks)).Text;

        Assert.Contains("query:           aubergine", text);
        Assert.Contains("scope:           Titles", text);
        Assert.Contains("limit:           3", text);
        Assert.Contains("caseSensitive:   True", text);
        Assert.Contains("tags:            urgent", text);
        Assert.Contains("contains=needle", text);

        // The untouched half of the form, still carrying the server's own defaults because nothing
        // was sent for them.
        Assert.Contains("offset:          0", text);
        Assert.Contains("includeArchived: True", text);
        Assert.Contains("apiToken:        (none)", text);
    }

    [Fact]
    public async Task A_default_the_user_turned_off_is_sent_as_false()
    {
        var search = Tool("demo_search");
        var state = FormState.Create(search.Form);

        ((TextFieldState)state.Find("query")!).Text = "x";
        var scope = (ChoiceFieldState)state.Find("scope")!;
        scope.Selected = scope.Options.Single(o => o.ValueText == "Everything");

        ((BooleanFieldState)state.Find("includeArchived")!).Value = false;

        var outcome = await Connection.CallAsync(search, state.Build().Arguments,
            TestContext.Current.CancellationToken);

        Assert.Contains("includeArchived: False",
            Assert.IsType<TextResultBlock>(Assert.Single(outcome.Blocks)).Text);
    }

    [Fact]
    public async Task An_argument_the_server_rejects_comes_back_as_an_error_rather_than_a_crash()
    {
        // Client-side validation is advice and never a veto (§5), so a call the client suspects is
        // wrong still goes out. What comes back has to be survivable.
        var search = Tool("demo_search");
        var arguments = new JsonObject { ["query"] = "x", ["scope"] = "NotAScope" };

        var outcome = await Connection.CallAsync(search, arguments, TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.NotEmpty(outcome.Blocks);
    }

    [Fact]
    public void The_fifteen_parameter_tool_renders_every_control_the_form_knows()
    {
        // The Phase 3 exit criterion's stand-in, and a guard on the fixture: if a control loses its
        // only real example, the manual pass stops covering it.
        var kinds = Tool("demo_search").Form.AllFields().Select(f => f.Kind).Distinct().ToList();

        Assert.Equal(15, Tool("demo_search").Form.Fields.Count);
        Assert.Equal(2, Tool("demo_search").Form.RequiredCount);

        foreach (var kind in (FieldKind[])
                 [
                     FieldKind.Text, FieldKind.MultilineText, FieldKind.Choice, FieldKind.Integer,
                     FieldKind.Number, FieldKind.Boolean, FieldKind.DateTime, FieldKind.PrimitiveList,
                     FieldKind.Group, FieldKind.ObjectList,
                 ])
        {
            Assert.Contains(kind, kinds);
        }
    }

    [Fact]
    public void The_wide_tool_is_wide_enough_to_catch_virtualization()
    {
        // Phase 0 measured 209 fields becoming 14 in the UI Automation tree with virtualization on
        // (§7.6). Forty is well past what one viewport realises, which is all the guard needs.
        Assert.Equal(40, Tool("demo_wide").Form.Fields.Count);
        Assert.Equal(0, Tool("demo_wide").Form.RequiredCount);
    }

    [Fact]
    public async Task A_tool_level_error_comes_back_as_an_error_rather_than_an_exception()
    {
        var outcome = await Connection.CallAsync(Tool("demo_fail"), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.Contains(outcome.Blocks.OfType<TextResultBlock>(), b => b.Text.Contains("always fails"));
    }

    [Fact]
    public async Task A_long_result_arrives_whole()
    {
        var outcome = await Connection.CallAsync(Tool("text_long"), TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextResultBlock>(Assert.Single(outcome.Blocks)).Text;

        Assert.Equal(200, text.Split('\n').Length);
        Assert.Contains("Line 200 of 200", text);
    }

    [Fact]
    public async Task A_server_that_will_not_start_is_a_state_rather_than_an_exception()
    {
        var descriptor = new ServerDescriptor(
            "broken", "broken server", "humcp-there-is-no-such-executable", []);

        await using var connection = new ServerConnection(descriptor);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.Failed, connection.State);
        Assert.False(string.IsNullOrWhiteSpace(connection.FailureReason));
        Assert.Empty(connection.Tools);

        // The tree node has to be able to say what went wrong (§8).
        Assert.StartsWith("broken server, error: ", ToolTreeBuilder.DescribeServer(connection.Snapshot()));
    }

    [Fact]
    public async Task The_tree_built_from_a_live_connection_reaches_four_levels()
    {
        await Task.CompletedTask;

        var server = ToolTreeBuilder.BuildServerNode(Connection.Snapshot());
        var tools = server.SelfAndDescendants().Where(n => n.Kind == CatalogueNodeKind.Tool).ToList();

        Assert.Equal(31, tools.Count);

        // Tools first, then the fixture's prompts (Phase 12).
        Assert.Equal(2, server.Children.Count);
        Assert.Contains(server.Children[0].Children, c => c.Kind == CatalogueNodeKind.Group);
    }

    // ------------------------------------------------------------------ the schema engine, on the wire

    [Fact]
    public void A_real_schema_off_the_wire_becomes_a_form()
    {
        // The corpus proves the engine against files. This proves it against a schema that has been
        // generated by the SDK, serialised, sent down a pipe and parsed back — which is the only
        // path the app ever actually uses.
        var form = Tool("demo_report").Form;

        Assert.Equal(["title", "order", "columns", "filter"], form.Fields.Select(f => f.Name));
        Assert.Equal(FieldKind.Text, form.Fields[0].Kind);
        Assert.Equal(FieldKind.Choice, form.Fields[1].Kind);
        Assert.Equal(FieldKind.PrimitiveList, form.Fields[2].Kind);
        Assert.Equal(FieldKind.Group, form.Fields[3].Kind);
        Assert.Equal(1, form.RequiredCount);
    }

    [Fact]
    public void A_nested_type_becomes_a_group_with_its_descriptions_intact()
    {
        // The C# SDK inlines a nested type rather than putting it in $defs, and marks it
        // `type: ["object", "null"]` when the parameter is optional. Both have to land as one
        // group that may be left empty — not as a union, and not as a JSON editor.
        var filter = Tool("demo_report").Form.Fields.Single(f => f.Name == "filter");

        Assert.Equal(FieldTier.Composite, filter.Tier);
        Assert.True(filter.AllowsNull);

        // camelCase, because the SDK publishes the property names as JSON rather than as C#.
        Assert.Equal(["filter.contains", "filter.limit"], filter.Fields.Select(f => f.Path));
        Assert.Equal("Only include rows containing this text.", filter.Fields[0].HelpText);
    }

    [Fact]
    public void A_recursive_type_degrades_rather_than_expanding_forever()
    {
        // Recursion off the wire is not the textbook shape. The SDK inlines the type one level and
        // then emits `$ref: "#/properties/tree/properties/child"` — a pointer into the schema's own
        // property tree rather than into $defs. The cycle is therefore caught a couple of levels
        // below where the recursion starts, which leaves the user two real groups to fill in and a
        // JSON editor under them. What matters is that it stops.
        var field = Tool("demo_tree").Form.Fields.Single();
        var depth = 0;

        while (field.Kind == FieldKind.Group)
        {
            Assert.True(++depth < 10, "The recursive schema was still expanding ten levels down.");
            field = field.Fields.Single(f => f.Name == "child");
        }

        Assert.Equal(FieldTier.Raw, field.Tier);
        Assert.Contains("itself", field.DegradedReason);
        Assert.Empty(field.Fields);
    }

    [Fact]
    public void A_form_built_from_a_listing_outlives_the_connection_that_produced_it()
    {
        // ToolInfo holds a FormModel rather than a JsonElement borrowed from the SDK's document,
        // so a tool can be shown after a reconnect has thrown that document away.
        var schema = Tool("demo_report").Form.Schema;

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out _));
    }

    [Fact]
    public void Client_side_validation_answers_in_sentences()
    {
        var report = SchemaValidator.Validate(Tool("demo_report").Form, new JsonObject());

        var issue = Assert.Single(report.Issues);
        Assert.Equal("Title: This one is required.", issue.Announcement);
    }
}

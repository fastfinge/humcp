using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// A prompt's argument list as the form it becomes (ROADMAP §11, Phase 12). Pure: no server.
/// </summary>
public class PromptFormTests
{
    private static ToolInfo Prompt(params PromptArgumentInfo[] arguments)
        => PromptForm.Build("review_code", "Review code", "Asks for a review. Then more.", arguments);

    [Fact]
    public void Every_argument_is_a_text_field_in_the_order_listed()
    {
        var prompt = Prompt(
            new("language", null, "The language.", true),
            new("framework", "Framework", null, false));

        Assert.Equal(["language", "framework"], prompt.Form.Fields.Select(f => f.Name));
        Assert.All(prompt.Form.Fields, f => Assert.Equal(FieldKind.Text, f.Kind));
        Assert.All(prompt.Form.Fields, f => Assert.Equal(FieldTier.Direct, f.Tier));
    }

    [Fact]
    public void Required_title_and_description_reach_the_field()
    {
        var prompt = Prompt(
            new("language", null, "The language the code is in.", true),
            new("framework", "Web framework", null, false));

        var language = prompt.Form.Fields[0];
        var framework = prompt.Form.Fields[1];

        Assert.True(language.IsRequired);
        Assert.Equal("Language", language.Label);
        Assert.Equal("The language the code is in.", language.HelpText);

        Assert.False(framework.IsRequired);
        Assert.Equal("Web framework", framework.Label);
        Assert.Equal(1, prompt.RequiredParameterCount);
    }

    [Fact]
    public void A_nameless_or_repeated_argument_is_not_drawn()
    {
        var prompt = Prompt(new("", null, null, true), new("a", null, null, false), new("a", "Again", null, true));

        var field = Assert.Single(prompt.Form.Fields);
        Assert.Equal("a", field.Name);
        Assert.False(field.IsRequired);
    }

    [Fact]
    public void A_prompt_is_named_as_one()
    {
        var prompt = Prompt(new PromptArgumentInfo("language", null, null, true));

        Assert.True(prompt.IsPrompt);
        Assert.Equal("Get prompt", prompt.RunVerb);

        // "Arguments", which is the protocol's word for them; and no safety, because fetching a
        // prompt changes nothing and saying "read-only" on every one would be noise.
        Assert.Equal("Review code, 1 required argument", prompt.AccessibleName);
        Assert.Equal("Asks for a review.", prompt.Summary);
    }

    [Fact]
    public void A_filled_in_form_sends_only_what_was_typed()
    {
        var state = FormState.Create(Prompt(
            new("language", null, null, true),
            new("framework", null, null, false)).Form);

        ((TextFieldState)state.Fields[0]).Text = "csharp";

        var arguments = state.Prepare().Arguments;

        Assert.Equal("""{"language":"csharp"}""", arguments.ToJsonString());
    }

    [Fact]
    public void An_empty_required_argument_is_reported_by_the_schema()
    {
        var state = FormState.Create(Prompt(new PromptArgumentInfo("language", null, null, true)).Form);

        var submission = state.Prepare();

        Assert.True(submission.HasIssues);
        Assert.True(submission.CanSend);
    }
}

/// <summary>Prompts in the tree, beside tools and never mixed with them.</summary>
public class PromptTreeTests
{
    private static ToolInfo Tool(string name) => new(name, name, "", "", OperationSafety.Unstated,
        FormBuilder.Build(JsonDocument.Parse("""{"type":"object"}""").RootElement));

    private static ServerSnapshot Server(IReadOnlyList<ToolInfo> tools, IReadOnlyList<ToolInfo> prompts)
        => new("test", ServerConnectionState.Connected, tools, "2026-07-28") { Prompts = prompts };

    [Fact]
    public void A_server_with_prompts_has_a_prompts_category_after_its_tools()
    {
        var node = ToolTreeBuilder.BuildServerNode(Server(
            [Tool("a"), Tool("b")],
            [PromptForm.Build("p", null, null, [])]));

        Assert.Equal(["Tools, 2 items", "Prompts, 1 item"], node.Children.Select(c => c.AccessibleName));

        var prompt = Assert.Single(node.Children[1].Children);
        Assert.Equal(CatalogueNodeKind.Prompt, prompt.Kind);
        Assert.True(prompt.Tool!.IsPrompt);
        Assert.Equal("test", prompt.ServerId);
    }

    [Fact]
    public void The_server_node_counts_prompts_only_when_there_are_some()
    {
        Assert.Equal("test, connected, 1 tool, 2 prompts, protocol 2026-07-28",
            ToolTreeBuilder.DescribeServer(Server([Tool("a")],
                [PromptForm.Build("p", null, null, []), PromptForm.Build("q", null, null, [])])));

        Assert.Equal("test, connected, no tools, 1 prompt, protocol 2026-07-28",
            ToolTreeBuilder.DescribeServer(Server([], [PromptForm.Build("p", null, null, [])])));

        Assert.Equal("test, connected, 1 tool, protocol 2026-07-28",
            ToolTreeBuilder.DescribeServer(Server([Tool("a")], [])));
    }

    [Fact]
    public void Several_servers_count_their_prompts_too()
    {
        // Found by ear (phase12-notes): the one-server sentence counted prompts and this did not.
        var a = Server([Tool("a")], [PromptForm.Build("p", null, null, [])]);
        var b = Server([Tool("b")], []) with { DisplayName = "other" };

        Assert.Equal("Connected to 2 servers, 2 tools, 1 prompt.", ServerAnnouncements.Startup([a, b]));
        Assert.Equal("Connected to test. 1 tool, 1 prompt, protocol 2026-07-28.", ServerAnnouncements.Startup([a]));
    }

    [Fact]
    public void A_prompt_form_keeps_its_own_commands_letters()
    {
        // Get prompt is G and the Prompt menu P; an argument must not share either (§7.21).
        var prompt = PromptForm.Build("x", null, null,
            [new("goal", null, null, true), new("path", null, null, true)]);

        var state = FormState.Create(prompt.Form, AccessKeys.PromptCommands);

        foreach (var field in state.Fields)
        {
            var key = field.AccessKeyIndex is { } i ? char.ToLowerInvariant(field.Label[i]) : (char?)null;
            Assert.NotEqual('g', key);
            Assert.NotEqual('p', key);
        }
    }

    [Fact]
    public void A_prompt_list_changing_is_said_in_its_own_words()
    {
        var before = new[] { PromptForm.Build("p", "Plan", null, []) };
        var after = new[] { PromptForm.Build("p", "Plan", null, []), PromptForm.Build("q", "Quiz", null, []) };

        Assert.Equal("test changed its prompts: 1 added, Quiz. 2 prompts now.",
            ServerAnnouncements.ToolsChanged("test", before, after, "prompt"));
    }
}

/// <summary>
/// Prompts over a real pipe: listing, fetching, and the server's suggestions for an argument.
/// </summary>
public class PromptRoundTripTests(TestServerFixture fixture) : IClassFixture<TestServerFixture>
{
    private ServerConnection Connection => fixture.Connection;

    private ToolInfo Prompt(string name)
        => Connection.Prompts.SingleOrDefault(p => p.Name == name)
           ?? throw new InvalidOperationException(
               $"The fixture published no prompt called {name}. It published: " +
               string.Join(", ", Connection.Prompts.Select(p => p.Name)));

    [Fact]
    public void Lists_every_prompt_beside_the_tools()
    {
        Assert.Equal(4, Connection.Prompts.Count);
        Assert.All(Connection.Prompts, p => Assert.True(p.IsPrompt));

        // A prompt may share a name with a tool, and both are there.
        Assert.Contains(Connection.Tools, t => t.Name == "demo_echo");
        Assert.Contains(Connection.Prompts, p => p.Name == "demo_echo");

        Assert.True(Connection.SupportsCompletion);
        Assert.Equal(4, Connection.Snapshot().Prompts.Count);
    }

    [Fact]
    public void A_prompts_arguments_become_its_form()
    {
        var review = Prompt("review_code");

        Assert.Equal("Review code", review.DisplayName);
        Assert.Equal(["language", "framework", "depth"], review.Form.Fields.Select(f => f.Name));
        Assert.True(review.Form.Fields[0].IsRequired);
        Assert.False(review.Form.Fields[1].IsRequired);
    }

    [Fact]
    public async Task Fetching_a_prompt_gives_its_messages_with_their_speakers()
    {
        var outcome = await Connection.CallAsync(Prompt("review_code"),
            new JsonObject { ["language"] = "csharp", ["framework"] = "wpf" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);
        Assert.Equal(["User", "Assistant"], outcome.Blocks.Select(b => b.Role));

        var first = Assert.IsType<TextResultBlock>(outcome.Blocks[0]);
        Assert.Contains("csharp code using wpf", first.Text);

        // Line structure survives, as it does for a tool's text (§7.19).
        Assert.Contains('\n', first.Text);
    }

    [Fact]
    public async Task A_prompt_with_no_arguments_is_fetched_with_none()
    {
        var outcome = await Connection.CallAsync(Prompt("greeting"), TestContext.Current.CancellationToken);

        var block = Assert.IsType<TextResultBlock>(Assert.Single(outcome.Blocks));
        Assert.Equal("User", block.Role);
    }

    [Fact]
    public async Task A_picture_in_a_prompt_arrives_as_a_picture()
    {
        var outcome = await Connection.CallAsync(Prompt("conversation"), TestContext.Current.CancellationToken);

        Assert.Equal(["User", "User", "Assistant"], outcome.Blocks.Select(b => b.Role));
        Assert.Equal(MediaKind.Image, Assert.IsType<MediaResultBlock>(outcome.Blocks[1]).Kind);
    }

    [Fact]
    public async Task The_prompt_and_the_tool_of_one_name_are_different_requests()
    {
        var prompt = await Connection.CallAsync(Prompt("demo_echo"), new JsonObject { ["words"] = "hello" },
            TestContext.Current.CancellationToken);

        Assert.Equal("Repeat after me: hello", Assert.IsType<TextResultBlock>(Assert.Single(prompt.Blocks)).Text);
    }

    [Fact]
    public async Task A_missing_required_argument_is_the_servers_refusal_in_a_sentence()
    {
        var outcome = await Connection.CallAsync(Prompt("review_code"), new JsonObject(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.StartsWith("Review code could not be fetched.", outcome.CombinedText);
    }

    [Fact]
    public async Task The_server_suggests_values_for_an_argument_as_it_is_typed()
    {
        var all = await Connection.CompleteAsync(Prompt("review_code"), "language", "",
            cancellationToken: TestContext.Current.CancellationToken);
        var narrowed = await Connection.CompleteAsync(Prompt("review_code"), "language", "py",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["csharp", "fsharp", "python", "rust", "typescript"], all);
        Assert.Equal(["python"], narrowed);
    }

    [Fact]
    public async Task What_the_other_arguments_hold_narrows_the_suggestions()
    {
        var prompt = Prompt("review_code");

        var forPython = await Connection.CompleteAsync(prompt, "framework", "",
            new Dictionary<string, string> { ["language"] = "python" }, TestContext.Current.CancellationToken);

        Assert.Equal(["django", "fastapi", "flask"], forPython);
    }

    [Fact]
    public async Task A_tool_is_never_completed()
    {
        // There is no ref/tool (§4, pitfall 3). Asking would be an error for nothing.
        var tool = Connection.Tools.Single(t => t.Name == "demo_echo");

        Assert.Empty(await Connection.CompleteAsync(tool, "text", "", cancellationToken: TestContext.Current.CancellationToken));
    }
}

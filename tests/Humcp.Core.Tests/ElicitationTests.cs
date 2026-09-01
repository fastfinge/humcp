using System.Text.Json.Nodes;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// Multi round-trip requests and elicitation, over a real pipe to a real child process
/// (ROADMAP §10, Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// Every one of these drives the whole exchange: the fixture's tool throws
/// <c>InputRequiredException</c>, the SDK resolves the question through humcp's handler, and the
/// original call is retried with the state echoed back and a new JSON-RPC id. Nothing here mocks
/// the protocol, so what is asserted is what a server would see.
/// </para>
/// <para>
/// The prompter stands in for the window. What it is handed is exactly what
/// <see cref="Humcp.App"/> is handed, which is the point of the neutral models (§6.1): these tests
/// can say what the user will be shown without a desktop.
/// </para>
/// </remarks>
public class ElicitationTests(TestServerFixture fixture) : IClassFixture<TestServerFixture>
{
    private ServerConnection Connection => fixture.Connection;

    private ToolInfo Tool(string name)
        => Connection.Tools.SingleOrDefault(t => t.Name == name)
           ?? throw new InvalidOperationException(
               $"The fixture published no tool called {name}. It published: " +
               string.Join(", ", Connection.Tools.Select(t => t.Name)));

    /// <summary>A prompter that records what it was asked and answers from a script.</summary>
    private sealed class Scripted(Func<ElicitationRequest, ElicitationAnswer> answer)
    {
        public List<ElicitationRequest> Asked { get; } = [];

        public ElicitationPrompter Prompter => (request, _) =>
        {
            Asked.Add(request);
            return Task.FromResult(answer(request));
        };
    }

    private static ElicitationAnswer Fill(ElicitationRequest request, params (string Name, JsonNode? Value)[] values)
    {
        Assert.Equal(ElicitationKind.Form, request.Kind);

        var content = new JsonObject();
        foreach (var (name, value) in values) content[name] = value;

        return ElicitationAnswer.Accepted(content);
    }

    // ------------------------------------------------------------------ the exit criterion

    [Fact]
    public async Task A_tool_that_asks_twice_completes_through_two_forms()
    {
        var script = new Scripted(request => request.Message.Contains("sit", StringComparison.Ordinal)
            ? Fill(request, ("position", "aisle"))
            : Fill(request, ("departure", "2026-09-01"), ("passengers", 2), ("flexible", true)));

        var outcome = await Connection.CallAsync(
            Tool("mrtr_book"),
            new JsonObject { ["destination"] = "Perth" },
            script.Prompter,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);

        // Two questions, in order, each one a form the schema engine rendered natively.
        Assert.Equal(2, script.Asked.Count);
        Assert.Contains("travel to Perth", script.Asked[0].Message, StringComparison.Ordinal);
        Assert.Contains("sit", script.Asked[1].Message, StringComparison.Ordinal);
        Assert.All(script.Asked, r => Assert.Equal(FieldTier.Direct, r.Form.HighestTier));

        // And the answer could only have been produced by carrying the first round's answers
        // through the second: the server kept nothing between them.
        Assert.Contains("Perth", outcome.CombinedText, StringComparison.Ordinal);
        Assert.Contains("2026-09-01", outcome.CombinedText, StringComparison.Ordinal);
        Assert.Contains("aisle", outcome.CombinedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_form_carries_the_fields_the_server_described()
    {
        var script = new Scripted(_ => ElicitationAnswer.Cancelled);

        await Connection.CallAsync(Tool("mrtr_book"), new JsonObject { ["destination"] = "Perth" },
            script.Prompter, TestContext.Current.CancellationToken);

        var form = Assert.Single(script.Asked).Form;

        Assert.Equal(["departure", "passengers", "flexible"], form.Fields.Select(f => f.Name));
        Assert.Equal(["Departure date", "Passengers", "Flexible dates"], form.Fields.Select(f => f.Label));
        Assert.Equal([true, true, false], form.Fields.Select(f => f.IsRequired));
        Assert.Equal([FieldKind.Date, FieldKind.Integer, FieldKind.Boolean], form.Fields.Select(f => f.Kind));
    }

    // ------------------------------------------------------------------ the three answers

    [Theory]
    [InlineData(ElicitationAction.Accept, "accept")]
    [InlineData(ElicitationAction.Decline, "decline")]
    [InlineData(ElicitationAction.Cancel, "cancel")]
    public async Task All_three_answers_reach_the_server(ElicitationAction action, string expected)
    {
        var script = new Scripted(_ => new ElicitationAnswer(action));

        var outcome = await Connection.CallAsync(Tool("mrtr_confirm"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);
        Assert.Contains($"\"{expected}\"", outcome.CombinedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_question_with_no_fields_is_still_a_question()
    {
        var script = new Scripted(_ => ElicitationAnswer.Declined);

        await Connection.CallAsync(Tool("mrtr_confirm"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(script.Asked);

        Assert.False(request.HasFields);
        Assert.Contains("pretend to delete", request.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the schema subset

    [Fact]
    public async Task Every_kind_in_the_elicitation_subset_renders_natively()
    {
        var script = new Scripted(_ => ElicitationAnswer.Cancelled);

        await Connection.CallAsync(Tool("mrtr_kinds"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        var form = Assert.Single(script.Asked).Form;

        // ROADMAP §3 says Tier 1 is a superset of the elicitation subset. This is the whole subset,
        // built through the SDK's own types and serialised the way the wire carries it, so the
        // claim is measured rather than assumed (phase5-notes 3.2).
        Assert.Equal(13, form.Fields.Count);
        Assert.Equal(FieldTier.Direct, form.HighestTier);
        Assert.All(form.Fields, f => Assert.Null(f.DegradedReason));

        FieldModel Field(string name) => form.Fields.Single(f => f.Name == name);

        Assert.Equal(FieldKind.Text, Field("plain").Kind);
        Assert.Equal(FieldKind.Date, Field("day").Kind);
        Assert.Equal(FieldKind.DateTime, Field("moment").Kind);
        Assert.Equal(FieldKind.Integer, Field("count").Kind);
        Assert.Equal(FieldKind.Number, Field("ratio").Kind);
        Assert.Equal(FieldKind.Boolean, Field("agreed").Kind);

        // The two string formats the subset allows beyond date and date-time stay text boxes and
        // carry the format as a constraint, which is what the help text and the validator read.
        Assert.Equal(FieldKind.Text, Field("email").Kind);
        Assert.Equal("email", Field("email").Constraints.Format);
        Assert.Equal(FieldKind.Text, Field("website").Kind);
        Assert.Equal("uri", Field("website").Constraints.Format);

        // The two titled enumerations are the ones worth watching: on the wire they are `oneOf` and
        // `anyOf` of constants, which is the shape §5 sends to a JSON editor unless it recognises
        // that every branch is a value rather than a shape.
        Assert.Equal(FieldKind.Choice, Field("size").Kind);
        Assert.Equal(FieldKind.Choice, Field("urgency").Kind);
        Assert.Equal(FieldKind.PrimitiveList, Field("colours").Kind);
        Assert.Equal(FieldKind.PrimitiveList, Field("regions").Kind);
        Assert.Equal(FieldKind.Choice, Field("legacy").Kind);

        // And the labels survive, which is the entire reason a titled enum exists.
        Assert.Equal(["Drop everything", "This week", "Whenever"],
            Field("urgency").Choices.Select(c => c.Label));
        Assert.Equal(["Ascending", "Descending"], Field("legacy").Choices.Select(c => c.Label));
        Assert.Equal(["Ireland", "Virginia"],
            Field("regions").ItemField!.Choices.Select(c => c.Label));
    }

    [Fact]
    public async Task A_stated_default_is_sent_whether_or_not_the_user_touched_it()
    {
        // humcp's own rule is the opposite one (ROADMAP §5, "what a filled-in form means"): a value
        // the user did not choose is not sent, and FormState.Prepare honours that here exactly as
        // it does for a tool's arguments. It does not survive the crossing. The client SDK fills
        // every property whose schema stated a `default` back into the elicitation content before
        // it goes on the wire, and this test is what says so out loud (phase5-notes 3.3).
        var script = new Scripted(request =>
        {
            var state = FormState.Create(request.Form);
            state.Fields.OfType<TextFieldState>().Single(f => f.Path == "plain").Text = "typed";

            var arguments = state.Prepare().Arguments;

            // What humcp asked to send: one property, the one that was typed into.
            Assert.Equal(["plain"], arguments.Select(p => p.Key));

            return ElicitationAnswer.Accepted(arguments);
        });

        var outcome = await Connection.CallAsync(Tool("mrtr_kinds"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);

        // What the server was told, straight off the wire. Three properties nobody typed into.
        Assert.Contains(
            """{"action":"accept","content":{"plain":"typed","count":7,"agreed":true,"size":"medium"}}""",
            outcome.CombinedText, StringComparison.Ordinal);

        // Which is why an elicitation form hides nothing: see ElicitationWindow. A field the user
        // never saw is a field the server is about to be told the user agreed to.
        Assert.DoesNotContain("urgency", outcome.CombinedText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what humcp will not do

    [Fact]
    public async Task A_request_for_a_language_model_is_declined_and_explained()
    {
        var script = new Scripted(_ => throw new InvalidOperationException("a form was shown for sampling"));

        var outcome = await Connection.CallAsync(Tool("mrtr_sampling"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.Contains("a language model", outcome.CombinedText, StringComparison.Ordinal);
        Assert.Empty(script.Asked);
    }

    [Fact]
    public async Task A_request_for_roots_is_declined_and_explained()
    {
        var script = new Scripted(_ => throw new InvalidOperationException("a form was shown for roots"));

        var outcome = await Connection.CallAsync(Tool("mrtr_roots"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.Contains("folders", outcome.CombinedText, StringComparison.Ordinal);
        Assert.Empty(script.Asked);
    }

    [Fact]
    public async Task An_out_of_band_web_request_arrives_as_something_to_explain()
    {
        var script = new Scripted(_ => ElicitationAnswer.Declined);

        var outcome = await Connection.CallAsync(Tool("mrtr_url"), null, script.Prompter,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(script.Asked);

        // humcp declares form mode only, and a server can ask anyway. It reaches the shell as a
        // request of a different kind, with no form on it and the address it wanted to send the
        // user to — which is shown, and never followed.
        Assert.Equal(ElicitationKind.WebPage, request.Kind);
        Assert.Equal("https://example.com/authorise?token=not-a-real-token", request.Url);
        Assert.False(request.HasFields);
        Assert.Contains("Sign in on the web", request.Message, StringComparison.Ordinal);

        Assert.False(outcome.IsError, outcome.CombinedText);
        Assert.Contains("\"decline\"", outcome.CombinedText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ round trips nobody sees

    [Fact]
    public async Task A_round_trip_that_asks_nothing_shows_nothing()
    {
        var script = new Scripted(_ => throw new InvalidOperationException("a form was shown for a silent round trip"));

        var outcome = await Connection.CallAsync(Tool("mrtr_state_only"),
            new JsonObject { ["rounds"] = 2 }, script.Prompter, TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);
        Assert.Contains("Came back 2 times", outcome.CombinedText, StringComparison.Ordinal);
        Assert.Empty(script.Asked);
    }

    [Fact]
    public async Task A_server_that_never_stops_asking_is_stopped_for_it()
    {
        // A silent round trip is invisible to this client — the handler is never called, so humcp
        // cannot count them. The SDK stops at ten, and what matters here is that a server which
        // would loop forever ends as a failed call with a message rather than as a hung window
        // (phase5-notes 3.5).
        var outcome = await Connection.CallAsync(Tool("mrtr_state_only"),
            new JsonObject { ["rounds"] = 200 }, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.Contains("kept asking", outcome.CombinedText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ two windows at once

    [Fact]
    public async Task Two_calls_at_once_each_get_their_own_questions()
    {
        // One connection, two tool windows, two questions in flight. The handler is registered once
        // per connection, so what routes a question back to the window that provoked it is the
        // AsyncLocal set inside CallAsync — and this is the test that says so.
        var arrived = 0;
        var bothAsked = new TaskCompletionSource();

        var first = new Scripted(_ => ElicitationAnswer.Declined);
        var second = new Scripted(_ => ElicitationAnswer.Declined);

        var calls = await Task.WhenAll(Ask("Aberdeen", first), Ask("Zagreb", second));

        Assert.All(calls, outcome => Assert.False(outcome.IsError, outcome.CombinedText));

        Assert.Contains("travel to Aberdeen", Assert.Single(first.Asked).Message, StringComparison.Ordinal);
        Assert.Contains("travel to Zagreb", Assert.Single(second.Asked).Message, StringComparison.Ordinal);

        Task<ToolCallOutcome> Ask(string destination, Scripted script) => Connection.CallAsync(
            Tool("mrtr_book"),
            new JsonObject { ["destination"] = destination },
            async (request, token) =>
            {
                // Neither call may answer until both have been asked, so the two questions really
                // are outstanding at the same time rather than merely close together.
                var answer = await script.Prompter(request, token);

                if (Interlocked.Increment(ref arrived) == 2) bothAsked.TrySetResult();
                await bothAsked.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

                return answer;
            },
            TestContext.Current.CancellationToken);
    }
}

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
        Connection = new ServerConnection(BuiltInServers.TestServer(AppContext.BaseDirectory));
        await Connection.ConnectAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
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
        Assert.Equal(10, Connection.Tools.Count);
    }

    [Fact]
    public async Task An_image_with_no_alt_text_is_described_as_an_object()
    {
        var outcome = await Connection.CallAsync(Tool("demo_image"), TestContext.Current.CancellationToken);

        // §4, pitfall 6: the protocol has nowhere to put alt text, so the honest presentation is
        // the object itself — kind, type and size — rather than silence.
        var block = Assert.IsType<ObjectResultBlock>(Assert.Single(outcome.Blocks));
        Assert.StartsWith("Image, image/png, ", block.Description);
        Assert.Contains("bytes", block.Description);
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

        Assert.Equal(10, tools.Count);
        Assert.Contains(server.Children.Single().Children, c => c.Kind == CatalogueNodeKind.Group);
    }
}

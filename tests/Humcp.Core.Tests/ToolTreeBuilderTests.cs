using System.Text.Json;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// The tree of ROADMAP §8, and the prefix-grouping heuristic §13 records as a guess worth
/// arguing with. Arguing with it here is cheaper than arguing with it by ear.
/// </summary>
public class ToolTreeBuilderTests
{
    [Fact]
    public void A_connected_server_says_its_tool_count_and_protocol()
    {
        var snapshot = Connected(Tools("one_a", "one_b"), protocol: "2026-07-28");

        Assert.Equal("test, connected, 2 tools, protocol 2026-07-28", ToolTreeBuilder.DescribeServer(snapshot));
    }

    [Fact]
    public void A_failed_server_says_why_on_its_own_node()
    {
        var snapshot = new ServerSnapshot("test", ServerConnectionState.Failed, [],
            FailureReason: "process exited with code 1");

        Assert.Equal("test, error: process exited with code 1", ToolTreeBuilder.DescribeServer(snapshot));
    }

    [Fact]
    public void A_connecting_server_says_so()
    {
        var snapshot = new ServerSnapshot("test", ServerConnectionState.Connecting, []);

        Assert.Equal("test, connecting", ToolTreeBuilder.DescribeServer(snapshot));
    }

    [Fact]
    public void A_server_with_no_tools_has_no_category_node()
    {
        // §8: category nodes appear only when non-empty.
        var node = ToolTreeBuilder.BuildServerNode(Connected([]));

        Assert.Empty(node.Children);
    }

    [Fact]
    public void A_small_tool_set_is_left_flat()
    {
        // Six tools with three tidy prefixes: grouping would spend two arrow presses out of eight
        // on headers, so it does not happen.
        var tools = Tools("info_a", "info_b", "text_a", "text_b", "demo_a", "demo_b");

        var category = ToolTreeBuilder.BuildServerNode(Connected(tools)).Children.Single();

        Assert.Equal(CatalogueNodeKind.Category, category.Kind);
        Assert.All(category.Children, child => Assert.Equal(CatalogueNodeKind.Tool, child.Kind));
    }

    [Fact]
    public void A_large_prefixed_tool_set_is_grouped_four_levels_deep()
    {
        var tools = Tools(
            "info_server", "info_environment",
            "text_colours", "text_long", "text_hostile",
            "demo_echo", "demo_slow", "demo_fail", "demo_delete_everything");

        var server = ToolTreeBuilder.BuildServerNode(Connected(tools));
        var category = server.Children.Single();

        // server → Tools → group → tool. Four levels is the shape Phase 0 proved WPF keeps in the
        // UIA tree, and the shape WinUI 3 could not (§2.1).
        Assert.Equal(4, Depth(server));
        Assert.Equal(3, category.Children.Count(c => c.Kind == CatalogueNodeKind.Group));
        Assert.Contains(category.Children, c => c.AccessibleName == "Demo, 4 items");
        Assert.Contains(category.Children, c => c.AccessibleName == "Info, 2 items");
        Assert.Contains(category.Children, c => c.AccessibleName == "Text, 3 items");
    }

    [Fact]
    public void A_tool_whose_prefix_is_unique_sits_beside_the_groups()
    {
        var tools = Tools(
            "info_server", "info_environment",
            "text_colours", "text_long", "text_hostile",
            "demo_echo", "demo_slow",
            "ping");

        var category = ToolTreeBuilder.BuildServerNode(Connected(tools)).Children.Single();

        // No "Miscellaneous" bucket: an ungrouped tool is one arrow press from the category, not two.
        Assert.Contains(category.Children, c => c.Kind == CatalogueNodeKind.Tool && c.Tool?.Name == "ping");
    }

    [Fact]
    public void Every_node_has_something_to_say()
    {
        var server = ToolTreeBuilder.BuildServerNode(Connected(Tools(
            "info_server", "info_environment", "text_colours", "text_long", "text_hostile",
            "demo_echo", "demo_slow", "demo_fail", "demo_delete_everything")));

        Assert.All(server.SelfAndDescendants(), node => Assert.False(string.IsNullOrWhiteSpace(node.AccessibleName)));
    }

    [Fact]
    public void A_destructive_tool_says_so_before_the_user_presses_enter()
    {
        var tool = Tool("demo_delete_everything", safety: new OperationSafety(false, true, false));

        Assert.Equal("Demo delete everything, destructive", tool.AccessibleName);
    }

    [Fact]
    public void A_tool_with_required_parameters_says_how_many()
    {
        var tool = Tool("demo_echo", parameters: 2, required: 1);

        Assert.Equal("Demo echo, 1 required parameter", tool.AccessibleName);
        Assert.False(tool.RunsWithoutArguments);
    }

    [Fact]
    public void A_tool_with_only_optional_parameters_can_still_be_run()
    {
        var tool = Tool("text_long", parameters: 3, required: 0);

        Assert.Equal("Text long, 3 optional parameters", tool.AccessibleName);
        Assert.True(tool.RunsWithoutArguments);
    }

    // ------------------------------------------------------------------ helpers

    private static int Depth(CatalogueNode node)
        => node.Children.Count == 0 ? 1 : 1 + node.Children.Max(Depth);

    private static ServerSnapshot Connected(IReadOnlyList<ToolInfo> tools, string? protocol = null)
        => new("test", ServerConnectionState.Connected, tools, protocol);

    private static IReadOnlyList<ToolInfo> Tools(params string[] names) => [.. names.Select(n => Tool(n))];

    private static ToolInfo Tool(
        string name, int parameters = 0, int required = 0, OperationSafety? safety = null)
        => new(
            Name: name,
            DisplayName: NameHumanizer.Humanize(name),
            Summary: "",
            FullDescription: "",
            Safety: safety ?? OperationSafety.Unstated,
            ParameterCount: parameters,
            RequiredParameterCount: required,
            InputSchema: EmptySchema);

    private static readonly JsonElement EmptySchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
}

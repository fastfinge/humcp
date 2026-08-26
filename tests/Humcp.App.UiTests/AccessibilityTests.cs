using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// The mechanical half of the accessibility contract (ROADMAP §7, §9), asserted against the live
/// UIA tree of the running application. Automated checks prove names exist; only NVDA, JAWS and
/// Narrator prove the app is pleasant, so this is a floor and not a verdict.
/// </summary>
[Collection(AppCollection.Name)]
public class AccessibilityTests(AppFixture fixture, ITestOutputHelper output)
{
    private Window Window => fixture.Window;

    [Fact]
    public void The_uia_tree_is_dumped_for_review()
    {
        var dump = UiaTree.Dump(Window);
        output.WriteLine(dump);

        var path = Path.Combine(AppContext.BaseDirectory, "uia-tree-report.txt");
        File.WriteAllText(path, dump);
        output.WriteLine("Written to " + path);

        Assert.Contains("humcp", dump);
    }

    [Fact]
    public void The_tree_hierarchy_survives_into_the_uia_tree()
    {
        var tree = Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Assert.NotNull(tree);

        var items = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));
        var byDepth = items
            .GroupBy(item => UiaTree.StructuralDepth(item, tree))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (depth, count) in byDepth.OrderBy(p => p.Key))
            output.WriteLine($"structural depth {depth}: {count} item(s)");

        // server → Tools → prefix group → tool. This is the failure that ruled out WinUI 3, where
        // every node announced at level 1 (ROADMAP §2.1), so it is asserted on every build.
        Assert.True(byDepth.Count >= 4,
            $"Expected at least 4 nesting depths in the tree, saw {byDepth.Count}.");

        // Depth is asserted structurally on purpose: WPF does not implement the UIA Level property,
        // so an assertion on it would pass vacuously forever.
        Assert.All(items, item => Assert.Null(UiaTree.LevelOf(item)));
    }

    [Fact]
    public void The_server_node_says_its_status_and_negotiated_protocol()
    {
        var tree = Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        var server = tree?.FindFirstChild(cf => cf.ByControlType(ControlType.TreeItem));
        Assert.NotNull(server);

        output.WriteLine(server.Name);

        // §8: status is part of the accessible name, so it is heard on arrow-through.
        Assert.StartsWith("humcp test server, connected,", server.Name);
        Assert.Contains("21 tools", server.Name);
        Assert.Contains("protocol ", server.Name);
    }

    [Fact]
    public void Every_keyboard_focusable_element_has_a_name()
    {
        var missing = new List<string>();
        var checkedCount = 0;

        foreach (var element in UiaTree.Descendants(Window))
        {
            var type = UiaTree.Safe(() => element.ControlType);

            // Framework chrome, named explicitly rather than by loosening the rule that catches
            // our own unnamed controls (ROADMAP §9).
            if (type is ControlType.TitleBar or ControlType.StatusBar) continue;

            if (UiaTree.Safe(() => element.Properties.IsKeyboardFocusable.ValueOrDefault) != true) continue;

            checkedCount++;
            if (string.IsNullOrWhiteSpace(UiaTree.Safe(() => element.Name)))
                missing.Add($"{type} (automationId={UiaTree.Safe(() => element.AutomationId)})");
        }

        output.WriteLine($"Keyboard-focusable elements checked: {checkedCount}");
        Assert.True(checkedCount > 3, "Suspiciously few focusable elements; the window may not be ready.");
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_edit_control_carries_help_text_or_a_label()
    {
        var bad = new List<string>();

        foreach (var element in UiaTree.Descendants(Window))
        {
            if (UiaTree.Safe(() => element.ControlType) is not
                (ControlType.Edit or ControlType.ComboBox or ControlType.CheckBox)) continue;

            var help = UiaTree.Safe(() => element.HelpText) ?? "";
            var labelledBy = UiaTree.Safe(() => element.Properties.LabeledBy.ValueOrDefault);

            if (string.IsNullOrWhiteSpace(help) && labelledBy is null)
                bad.Add(UiaTree.Safe(() => element.Name) ?? "(unnamed)");
        }

        Assert.Empty(bad);
    }

    [Fact]
    public void The_result_pane_is_a_read_only_edit_rather_than_static_text()
    {
        var result = ResultBox();

        // §7.9: a TextBox gives caret navigation, so output can be reviewed line by line. A
        // TextBlock would be a Text element with none of that, and it is the easy mistake.
        Assert.Equal(ControlType.Edit, result.ControlType);
        Assert.True(result.Patterns.Value.Pattern.IsReadOnly);
        Assert.True(result.Properties.IsKeyboardFocusable);
        Assert.False(string.IsNullOrWhiteSpace(result.HelpText));
    }

    [Fact]
    public void The_tab_order_reaches_the_real_controls_and_contains_nothing_else()
    {
        Input.EnsureForeground(Window, fixture.Automation);

        // Start on a runnable tool. A TreeView is not itself focusable once it has items — focus
        // belongs to the selected item — and the Run button is correctly out of the tab order
        // while it is disabled, so the walk starts from a state where everything real is live.
        var tree = Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Assert.NotNull(tree);

        var tool = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .First(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith("Server information", StringComparison.Ordinal));
        tool.Focus();
        Input.Settle(150);

        var stops = Input.WalkTabOrder(Window, fixture.Automation, out var cycled);
        foreach (var stop in stops) output.WriteLine(stop);

        // Both directions of the rule (§7.7 and §7.14): everything real is reachable…
        Assert.Contains(stops, s => s.StartsWith("TreeItem:"));
        Assert.Contains(stops, s => s == "Button:Run tool");
        Assert.Contains(stops, s => s == "Button:Reconnect");
        Assert.Contains(stops, s => s.StartsWith("Edit:Result"));

        // …and nothing else is. A menu bar is reached with Alt and arrowed between; a splitter is
        // reached from a command (phase0-findings 2.4, 2.5).
        Assert.DoesNotContain(stops, s => s.StartsWith("Thumb:"));
        Assert.DoesNotContain(stops, s => s.StartsWith("MenuItem:"));

        Assert.True(cycled, "The tab order never returned to its first stop.");
    }

    /// <summary>
    /// The text box of the first part of the result. Since Phase 4 a result is a set of controls
    /// rather than one box, so this reaches through the view — which is named differently from the
    /// form window's view, because an owned window is a UIA descendant of this one (§9).
    /// </summary>
    private AutomationElement ResultBox()
        => Window.FindFirstDescendant(cf => cf.ByAutomationId("MainResults"))
               ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))
           ?? throw new InvalidOperationException("The result box is not in the UIA tree.");
}

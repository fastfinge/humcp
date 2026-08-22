using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// Phase 1's exit criterion (ROADMAP §10): an end-to-end round trip, driven entirely by the
/// keyboard. Every step here is a key a user would press.
/// </summary>
[Collection(AppCollection.Name)]
public class RoundTripTests(AppFixture fixture, ITestOutputHelper output)
{
    private Window Window => fixture.Window;

    [Fact]
    public void Running_a_zero_argument_tool_from_the_keyboard_shows_its_result()
    {
        PressEnterOn("Server information");

        var result = WaitForResult(text => text.Contains("humcp test server"));
        output.WriteLine(UiaTree.Flatten(result, 200));

        Assert.Contains("Server information — complete", result);
        Assert.Contains("Process id:", result);

        // The result box takes the tool's name too, so a user arriving by F6 knows what they are
        // reading before they start reading it.
        Assert.Contains("Server information", ResultBox().Name);
    }

    [Fact]
    public void A_long_result_arrives_whole_and_is_reviewable()
    {
        PressEnterOn("Two hundred lines");

        var result = WaitForResult(text => text.Contains("Line 200 of 200"));

        Assert.Contains("Line   1 of 200", result);
        Assert.True(result.Split('\n').Length > 200);
    }

    [Fact]
    public void A_tool_level_error_is_announced_as_an_error()
    {
        PressEnterOn("Always fails");

        var result = WaitForResult(text => text.Contains("always fails"));

        Assert.Contains("— error at", result);
        Assert.Contains("always fails", result);
    }

    [Fact]
    public void A_tool_that_needs_parameters_opens_a_form_instead_of_being_declined()
    {
        // Phase 1 answered this with a sentence saying forms were not built yet (§7.18). Phase 3
        // is where that sentence was meant to stop being true, so the test that held it honest is
        // now the test that says so.
        PressEnterOn("Echo");

        var form = fixture.Owned("Echo — humcp test server", TimeSpan.FromSeconds(10));
        Assert.True(form is not null, "Enter on a tool with parameters opened no form.");

        output.WriteLine(form!.Name);

        try
        {
            // The form window has a status line of its own, and a different automation id, because
            // a form is a UIA descendant of the window that opened it: two elements called
            // "LiveStatus" would make every lookup from the main window a coin toss.
            var status = form.FindFirstDescendant(cf => cf.ByAutomationId("FormStatus"))?.Name ?? "";
            Assert.Contains("2 parameters, 1 required", status);
        }
        finally
        {
            form.Close();
            Input.Settle(200);
        }
    }

    [Fact]
    public void Typing_a_letter_in_the_tree_moves_to_the_next_tool_starting_with_it()
    {
        // Part of what Windows assumes of any list-shaped control, and WPF's TreeView is the one
        // that does not have it — a manual pass pressed S on a nine-tool server and went nowhere
        // (phase3-notes 4.10). This tree is sized by whatever a server publishes, so the difference
        // on a forty-tool one is a keystroke against forty of them.
        Input.EnsureForeground(Window, fixture.Automation);

        FindTreeItem("Always fails").Focus();
        Input.Settle(150);

        Input.Press(VirtualKeyShort.KEY_S);
        Input.Settle(400);

        var focused = fixture.Automation.FocusedElement();
        output.WriteLine(UiaTree.Safe(() => focused?.Name) ?? "(nothing)");

        Assert.StartsWith("Search documents", UiaTree.Safe(() => focused?.Name) ?? "");

        // Pressing it again goes to the next match rather than sticking.
        Input.Press(VirtualKeyShort.KEY_S);
        Input.Settle(400);

        Assert.StartsWith("Slow tool", UiaTree.Safe(() => fixture.Automation.FocusedElement()?.Name) ?? "");
    }

    [Fact]
    public void The_destructive_annotation_is_part_of_the_tool_name()
    {
        var item = FindTreeItem("Delete everything");

        // §8: heard on arrow-through, before Enter is pressed, rather than in a pane elsewhere.
        Assert.Equal("Delete everything, destructive", item.Name);
    }

    [Fact]
    public void F6_moves_between_the_panes()
    {
        Input.EnsureForeground(Window, fixture.Automation);

        FindTreeItem("humcp test server").Focus();
        Input.Settle(150);

        // Two presses at most: the pane cycle is two long, and where it starts depends on what the
        // previous test left focused.
        for (var i = 0; i < 2; i++)
        {
            Input.Press(VirtualKeyShort.F6);
            Input.Settle(150);

            if (fixture.Automation.FocusedElement()?.AutomationId == "ResultBox") return;
        }

        Assert.Fail("F6 never reached the result pane. Focus is on: " +
                    (fixture.Automation.FocusedElement()?.Name ?? "(nothing)"));
    }

    [Fact]
    public void F1_shows_the_whole_description_that_help_text_only_summarises()
    {
        Input.EnsureForeground(Window, fixture.Automation);

        var tool = FindTreeItem("Hostile text");
        var summary = tool.HelpText;
        tool.Focus();
        Input.Settle(150);

        Input.Press(VirtualKeyShort.F1);
        Input.Settle(400);

        var details = Window.FindFirstChild(cf => cf.ByControlType(ControlType.Window))?.AsWindow();
        Assert.NotNull(details);
        output.WriteLine($"details window name: \"{UiaTree.Flatten(details.Name, 60)}\"");

        // A WPF Window is a ContentControl, so with no explicit name its peer reports the plain
        // text of its content — the whole body announced as the window title. The name is set
        // explicitly for exactly this reason, and asserted here so it stays set.
        Assert.StartsWith("Details: Hostile text", details.Name);

        var body = details.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text ?? "";

        // ROADMAP §4, pitfall 1: the first sentence is the help text, and the ten thousand
        // characters behind it are here, on demand, rather than in anyone's ear.
        Assert.True(summary.Length < 200, $"Help text was {summary.Length} characters.");
        Assert.True(body.Length > 2000, $"The details window held only {body.Length} characters.");
        Assert.Contains("Input schema", body);

        details.Focus();
        Input.Press(VirtualKeyShort.ESCAPE);
        Input.Settle(300);

        // Specifically the details window. Since Phase 3 the app has other windows — a generated
        // form is owned by this one and shows up as its child — so "no child windows at all" would
        // be asserting something this test has no opinion about.
        Assert.DoesNotContain(
            Window.FindAllChildren(cf => cf.ByControlType(ControlType.Window)),
            w => (UiaTree.Safe(() => w.Name) ?? "").StartsWith("Details:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private void PressEnterOn(string toolNamePrefix)
    {
        Input.EnsureForeground(Window, fixture.Automation);

        var item = FindTreeItem(toolNamePrefix);
        item.Focus();
        Input.Settle(150);

        var focused = fixture.Automation.FocusedElement();
        Assert.StartsWith(toolNamePrefix, focused?.Name ?? "(nothing focused)");

        Input.Press(VirtualKeyShort.RETURN);
    }

    private AutomationElement FindTreeItem(string namePrefix)
    {
        var tree = Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))
                   ?? throw new InvalidOperationException("No tree in the window.");

        var item = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith(namePrefix, StringComparison.Ordinal));

        return item ?? throw new InvalidOperationException(
            $"No tree item starts with \"{namePrefix}\". The tree holds: " +
            string.Join(" | ", tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .Select(i => UiaTree.Safe(() => i.Name))));
    }

    private AutomationElement ResultBox()
        => Window.FindFirstDescendant(cf => cf.ByAutomationId("ResultBox"))
           ?? throw new InvalidOperationException("The result box is not in the UIA tree.");

    private string WaitForResult(Func<string, bool> predicate)
        => WaitFor(() => ResultBox().AsTextBox().Text, predicate, "result box");

    private string WaitForStatus(Func<string, bool> predicate)
        => WaitFor(
            () => Window.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"))?.Name ?? "",
            predicate, "status line");

    private static string WaitFor(Func<string> read, Func<string, bool> predicate, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var last = "";

        while (DateTime.UtcNow < deadline)
        {
            last = UiaTree.Safe(read) ?? "";
            if (predicate(last)) return last;
            Thread.Sleep(150);
        }

        throw new InvalidOperationException($"The {what} never said what was expected. It says: {last}");
    }
}

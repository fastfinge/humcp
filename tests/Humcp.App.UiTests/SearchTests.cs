using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;

namespace Humcp.App.UiTests;

/// <summary>
/// Tool search (ROADMAP §11, Phase 8): Ctrl+F reaches a box above the tree, a search puts ranked
/// tools in the tree's place, and Escape gives the tree back as it was.
/// </summary>
[Collection(AppCollection.Name)]
public class SearchTests(AppFixture fixture, ITestOutputHelper output)
{
    private Window Main => fixture.Window;

    [Fact]
    public void Control_F_reaches_the_search_box_and_says_its_key()
    {
        Input.EnsureForeground(Main, fixture.Automation);
        Tree().FindFirstDescendant(cf => cf.ByControlType(ControlType.TreeItem))!.Focus();
        Input.Settle(150);

        Input.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
        Input.Settle(200);

        Assert.Equal("Find a tool", UiaTree.Safe(() => fixture.Automation.FocusedElement()?.Name));
        Assert.Equal("Control plus F", Box().Properties.AcceleratorKey.ValueOrDefault);
    }

    [Fact]
    public void A_search_takes_the_tree_s_place_and_Escape_gives_it_back()
    {
        try
        {
            Search("colours");

            var results = WaitFor(Results, "the results to appear");
            var first = UiaTree.Children(results).First();
            output.WriteLine(first.Name);

            // Named with its server: a ranked list has no nesting to say it.
            Assert.StartsWith("List colours, humcp test server", first.Name);
            Assert.Null(UiaTree.Safe(() => Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))));

            // The count, once typing has stopped.
            WaitFor(() => Status().EndsWith("match.", StringComparison.Ordinal) || Status().EndsWith("matches.", StringComparison.Ordinal)
                ? Status() : null, "the match count");

            // The keyboard half: from the box to the best match, Escape back, Escape out.
            Input.EnsureForeground(Main, fixture.Automation);
            Input.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
            Input.Settle(150);

            // Enter, not Down Arrow, though both do this. A synthetic arrow key never reaches humcp
            // while NVDA is running on the test machine (phase8-notes), and Enter is the one that
            // would otherwise go untested. Down Arrow is checked by ear.
            Input.Press(VirtualKeyShort.RETURN);
            Input.Settle(200);
            Assert.StartsWith("List colours", UiaTree.Safe(() => fixture.Automation.FocusedElement()?.Name) ?? "");

            Input.Press(VirtualKeyShort.ESCAPE);
            Input.Settle(200);
            Assert.Equal("Find a tool", UiaTree.Safe(() => fixture.Automation.FocusedElement()?.Name));

            Input.Press(VirtualKeyShort.ESCAPE);
            Input.Settle(300);
            Assert.Equal(ControlType.TreeItem, UiaTree.Safe(() => fixture.Automation.FocusedElement()?.ControlType));
            Assert.Equal("", Box().Patterns.Value.Pattern.Value.Value);
            Assert.Null(Results());
        }
        finally
        {
            Search("");
        }
    }

    [Fact]
    public void Enter_on_a_result_opens_its_form()
    {
        Window? form = null;

        try
        {
            Search("search documents");
            var result = WaitFor(() => Results() is { } r ? UiaTree.Children(r).FirstOrDefault() : null, "a result");
            Assert.StartsWith("Search documents", result.Name);

            Input.EnsureForeground(Main, fixture.Automation);
            result.Focus();
            Input.Settle(150);
            Input.Press(VirtualKeyShort.RETURN);

            form = fixture.Owned("Search documents — humcp test server", TimeSpan.FromSeconds(10));
            Assert.True(form is not null, "Enter opened no form. Open: " + string.Join(", ", fixture.Titles()));
        }
        finally
        {
            form?.Close();
            Input.Settle(300);
            Search("");
        }
    }

    [Fact]
    public void A_search_that_finds_nothing_says_so_where_the_caret_can_reach_it()
    {
        try
        {
            Search("xylophone ");

            var message = WaitFor(() => Results() is { } r ? UiaTree.Children(r).FirstOrDefault() : null, "the message");
            Assert.StartsWith("No tools match xylophone", message.Name);

            // A sentence, not a tool: nothing to run.
            // By id: an open form has a Run tool button of its own, and is a descendant of this window (§9).
            Assert.False(Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))!.IsEnabled);
        }
        finally
        {
            Search("");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Setup through the Value pattern, which needs no foreground (§9).</summary>
    private void Search(string text)
    {
        Box().Patterns.Value.Pattern.SetValue(text);
        Input.Settle(150);
    }

    private AutomationElement Box() =>
        Main.FindFirstDescendant(cf => cf.ByAutomationId("SearchBox"))
        ?? throw new InvalidOperationException("The main window has no search box.");

    private AutomationElement? Results() => Main.FindFirstDescendant(cf => cf.ByAutomationId("SearchResults"));

    private AutomationElement Tree() =>
        Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))
        ?? throw new InvalidOperationException("The main window has no tree.");

    private string Status() => Main.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"))?.Name ?? "";

    private static T WaitFor<T>(Func<T?> read, string what) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var value = UiaTree.Safe(read);
            if (value is not null) return value;
            Thread.Sleep(150);
        }

        throw new InvalidOperationException($"Waited thirty seconds for {what} and it never happened.");
    }
}

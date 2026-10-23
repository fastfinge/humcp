using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// Prompts (ROADMAP §11, Phase 12): in the tree beside the tools, opened as the same generated form,
/// their messages shown with who says each, and the server's suggestions behind their arguments.
/// </summary>
/// <remarks>
/// Driven through UI Automation patterns throughout: nothing here is a claim about a keystroke, and
/// setup that types fails whenever something else holds the foreground (§9).
/// </remarks>
[Collection(AppCollection.Name)]
public class PromptTests(AppFixture fixture, ITestOutputHelper output)
{
    private const string ReviewForm = "Review code — humcp test server";

    private Window Main => fixture.Window;

    [Fact]
    public void Prompts_are_a_category_of_their_own_after_the_tools()
    {
        var server = Tree().FindFirstChild(cf => cf.ByControlType(ControlType.TreeItem));
        Assert.NotNull(server);

        Assert.Contains("31 tools, 4 prompts", server.Name);

        var categories = server.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem))
            .Select(c => UiaTree.Safe(() => c.Name))
            .ToList();

        output.WriteLine(string.Join(" | ", categories));
        Assert.Equal(["Tools, 31 items", "Prompts, 4 items"], categories);

        // Named like a tool, in the protocol's word for what a prompt takes.
        Assert.NotNull(TreeItem("Review code, 1 required argument"));
    }

    [Fact]
    public void A_prompt_opens_the_generated_form_with_its_own_words()
    {
        using var form = Open("Review code", ReviewForm);

        var run = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"));
        Assert.Equal("Get prompt", run?.Name);

        Assert.Equal("Arguments", form.Window.FindFirstDescendant(cf => cf.ByAutomationId("ParametersHeading"))?.Name?
            .Split(" — ")[0]);

        // A server that completes arguments puts a drop-down behind each of them, from the start.
        var language = form.Control("Language");
        Assert.True(language is not null, "No Language field. The form holds: " + form.Names());
        Assert.Equal(ControlType.ComboBox, UiaTree.Safe(() => language!.ControlType));
        Assert.Contains("The server suggests values", UiaTree.Safe(() => language!.HelpText) ?? "");
        Assert.True(UiaTree.Safe(() => language!.Properties.IsRequiredForForm.ValueOrDefault));
    }

    [Fact]
    public void The_servers_suggestions_fill_the_drop_down()
    {
        using var form = Open("Review code", ReviewForm);

        var language = form.Control("Language")!.AsComboBox();

        // Focus is what asks: through UI Automation, so no keystroke and no foreground is needed.
        language.Focus();

        var items = WaitFor(() =>
        {
            language.Expand();
            Input.Settle(200);
            var names = language.Items.Select(i => UiaTree.Safe(() => i.Name) ?? "").ToList();
            language.Collapse();
            return names.Count > 0 ? names : null;
        }, "the language suggestions");

        output.WriteLine(string.Join(" | ", items));
        Assert.Equal(["csharp", "fsharp", "python", "rust", "typescript"], items);
    }

    [Fact]
    public void An_empty_drop_down_says_so_rather_than_nothing()
    {
        // Found by ear (phase12-notes): Alt+Down on an argument the server had nothing for opened an
        // empty list, and NVDA said "blank" after help text that had just promised suggestions.
        // A keystroke, because what is asserted is what the keystroke does.
        Input.EnsureForeground(Main, fixture.Automation);

        using var form = Open("Echo, the prompt", "Echo, the prompt — humcp test server");

        var words = form.Control("Words");
        Assert.True(words is not null, "No Words field. The form holds: " + form.Names());

        words!.Focus();
        Input.Settle(800);
        Input.EnsureForeground(form.Window, fixture.Automation);
        Input.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);

        var status = WaitFor(() =>
        {
            var text = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormStatus"))?.Name ?? "";
            return text.Contains("no suggestions", StringComparison.Ordinal) ? text : null;
        }, "the no-suggestions message");

        Input.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Assert.Equal("The server has no suggestions for Words yet. Type something and try again.", status);
    }

    [Fact]
    public void Fetching_a_prompt_shows_each_message_with_its_speaker()
    {
        using var form = Open("Review code", ReviewForm);

        form.Control("Language")!.Patterns.Value.Pattern.SetValue("csharp");
        Input.Settle(200);

        form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))!.AsButton().Invoke();

        var results = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormResults"))!;

        var parts = WaitFor(() =>
        {
            var named = results.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Select(e => UiaTree.Safe(() => e.Name) ?? "")
                .ToList();
            return named.Count >= 2 ? named : null;
        }, "the prompt's messages");

        output.WriteLine(string.Join(" | ", parts));
        Assert.Equal(["User message 1 of 2", "Assistant message 2 of 2"], parts.Take(2));

        var status = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormStatus"))?.Name ?? "";
        Assert.StartsWith("Prompt fetched. 2 messages: user,", status);
    }

    [Fact]
    public void A_prompt_with_no_arguments_is_fetched_straight_from_the_tree()
    {
        TreeItem("A greeting")!.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(150);

        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))!.AsButton();
        WaitFor(() => run.IsEnabled ? run : null, "the main Run button");
        run.Invoke();

        var heading = WaitFor(() =>
        {
            var text = Main.FindFirstDescendant(cf => cf.ByAutomationId("ResultHeading"))?.Name ?? "";
            return text == "Result — A greeting" ? text : null;
        }, "the result heading");

        Assert.Equal("Result — A greeting", heading);

        var box = Main.FindFirstDescendant(cf => cf.ByAutomationId("MainResults"))!
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;

        Assert.Equal("User message", box.Name);
        Assert.Contains("Say hello", box.AsTextBox().Text);
    }

    // ------------------------------------------------------------------ helpers

    private AutomationElement Tree()
        => Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))
           ?? throw new InvalidOperationException("The main window has no tree.");

    private AutomationElement? TreeItem(string prefix)
        => Tree().FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith(prefix, StringComparison.Ordinal));

    private OpenWindow Open(string label, string title)
    {
        if (fixture.Owned(title, TimeSpan.FromMilliseconds(200)) is { } already) return new OpenWindow(already);

        var item = TreeItem(label);
        Assert.True(item is not null, $"No tree item called {label}.");

        item!.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(150);

        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))!.AsButton();
        WaitFor(() => run.IsEnabled ? run : null, "the main Run button");
        run.Invoke();

        var window = fixture.Owned(title, TimeSpan.FromSeconds(10));
        Assert.True(window is not null, $"{title} did not open. Windows: {string.Join(" | ", fixture.Titles())}");

        Input.Settle(300);
        return new OpenWindow(window!);
    }

    private static T WaitFor<T>(Func<T?> read, string what) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (UiaTree.Safe(read) is { } value) return value;
            Thread.Sleep(150);
        }

        throw new InvalidOperationException($"Timed out waiting for {what}.");
    }

    private sealed class OpenWindow(Window window) : IDisposable
    {
        public Window Window { get; } = window;

        private AutomationElement Fields =>
            Window.FindFirstDescendant(cf => cf.ByAutomationId("Fields"))
            ?? throw new InvalidOperationException("The form is not in the UIA tree.");

        /// <summary>The focusable control named <paramref name="label"/>, not its label (§9).</summary>
        public AutomationElement? Control(string label) =>
            UiaTree.Descendants(Fields).FirstOrDefault(e =>
                UiaTree.Safe(() => e.Name) == label
                && UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true);

        public string Names() => string.Join(" | ", UiaTree.Descendants(Fields)
            .Select(e => $"{UiaTree.Safe(() => e.ControlType)}:{UiaTree.Safe(() => e.Name)}"));

        public void Dispose()
        {
            try { Window.Close(); } catch { /* already gone */ }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && UiaTree.Safe(() => Window.IsAvailable) == true) Thread.Sleep(100);

            Input.Settle(200);
        }
    }
}

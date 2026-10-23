using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// Resources (ROADMAP §11, Phase 13): in the tree, read from it, a template filled in with the
/// server's suggestions, a tool's address parameter offering them, and a link in a result read back.
/// </summary>
/// <remarks>Driven through UI Automation patterns: nothing here is a claim about a keystroke.</remarks>
[Collection(AppCollection.Name)]
public class ResourceTests(AppFixture fixture, ITestOutputHelper output)
{
    private const string NoteForm = "A note by name — humcp test server";
    private const string LinkForm = "Link to a resource — humcp test server";
    private const string ReadMeWindow = "Read me — humcp test server";

    private Window Main => fixture.Window;

    [Fact]
    public void Resources_are_a_category_named_with_what_each_is()
    {
        var server = Tree().FindFirstChild(cf => cf.ByControlType(ControlType.TreeItem))!;
        var resources = server.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem))
            .Single(c => UiaTree.Safe(() => c.Name) == "Resources, 4 items");

        var names = resources.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem))
            .Select(i => UiaTree.Safe(() => i.Name) ?? "")
            .ToList();

        output.WriteLine(string.Join(" | ", names));

        // Fixed addresses by name, then the template, each saying what it is.
        Assert.Equal(
            ["A forecast, application/json", "A red square, image/png", "Read me, text/plain",
             "A note by name, template, 1 required argument"],
            names);
    }

    [Fact]
    public void A_resource_is_read_straight_from_the_tree()
    {
        Invoke("Read me, text/plain");

        var box = WaitFor(() => Main.FindFirstDescendant(cf => cf.ByAutomationId("MainResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) is { } edit
            && UiaTree.Safe(() => edit.Name) == "Resource, fixture://notes/readme" ? edit : null, "the read resource");

        Assert.Contains("The third is this one.", box.AsTextBox().Text);
        Assert.Equal("Result — Read me", Main.FindFirstDescendant(cf => cf.ByAutomationId("ResultHeading"))?.Name);
    }

    [Fact]
    public void A_template_is_a_form_whose_suggestions_come_from_the_server()
    {
        using var form = Open("A note by name", NoteForm);

        Assert.Equal("Read resource", form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))?.Name);

        var name = form.Control("Name")!.AsComboBox();
        Assert.Contains("The server suggests values", UiaTree.Safe(() => name.HelpText) ?? "");

        name.Focus();
        var items = WaitFor(() =>
        {
            name.Expand();
            Input.Settle(200);
            var listed = name.Items.Select(i => UiaTree.Safe(() => i.Name) ?? "").ToList();
            name.Collapse();
            return listed.Count > 0 ? listed : null;
        }, "the note names");

        Assert.Equal(["readme", "shopping", "todo"], items);

        name.Patterns.Value.Pattern.SetValue("shopping");
        Input.Settle(200);
        form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))!.AsButton().Invoke();

        var text = WaitFor(() => form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text is { } t
            && t.Contains("Braille paper") ? t : null, "the note");

        Assert.Contains("Milk", text);
    }

    [Fact]
    public void An_address_parameter_offers_the_servers_resources_and_its_link_reads_back()
    {
        using var form = Open("Link to a resource", LinkForm);

        var address = form.Control("Resource uri")!;
        Assert.True(address is not null, "No Resource uri field. The form holds: " + form.Names());
        Assert.Equal(ControlType.ComboBox, UiaTree.Safe(() => address.ControlType));
        Assert.Contains("this server's resources", UiaTree.Safe(() => address.HelpText) ?? "");

        var combo = address.AsComboBox();
        combo.Expand();
        Input.Settle(300);
        var offered = combo.Items.Select(i => UiaTree.Safe(() => i.Name) ?? "").ToList();
        combo.Collapse();
        Input.Settle(200);

        output.WriteLine(string.Join(" | ", offered));
        Assert.Contains("fixture://notes/readme", offered);
        Assert.DoesNotContain("fixture://notes/{name}", offered);

        address.Patterns.Value.Pattern.SetValue("fixture://notes/readme");
        Input.Settle(200);
        form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))!.AsButton().Invoke();

        var read = WaitFor(() => form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormResults"))
            ?.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => UiaTree.Safe(() => b.Name) == "Read resource"), "the Read resource button");

        read.AsButton().Invoke();

        var window = fixture.Owned(ReadMeWindow, TimeSpan.FromSeconds(10));
        Assert.True(window is not null, $"Reading the link opened no window. Windows: {string.Join(" | ", fixture.Titles())}");

        using var opened = new OpenWindow(window!);
        var text = WaitFor(() => window!.FindFirstDescendant(cf => cf.ByAutomationId("FormResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text is { } t
            && t.Contains("Read me first.") ? t : null, "the linked resource");

        Assert.Contains("three lines", text);
    }

    // ------------------------------------------------------------------ helpers

    private AutomationElement Tree()
        => Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))
           ?? throw new InvalidOperationException("The main window has no tree.");

    private AutomationElement? TreeItem(string prefix)
        => Tree().FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Selects a tree item and presses the main window's Run button, through UI Automation.</summary>
    private void Invoke(string label)
    {
        var item = TreeItem(label);
        Assert.True(item is not null, $"No tree item called {label}.");

        item!.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(150);

        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))!.AsButton();
        WaitFor(() => run.IsEnabled ? run : null, "the main Run button");
        run.Invoke();
    }

    private OpenWindow Open(string label, string title)
    {
        if (fixture.Owned(title, TimeSpan.FromMilliseconds(200)) is { } already) return new OpenWindow(already);

        Invoke(label);

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

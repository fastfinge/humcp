using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// A rendered result, asserted against the live UIA tree of a running application (ROADMAP §10,
/// Phase 4; §9).
/// </summary>
/// <remarks>
/// Phase 4's exit criterion is that every kind of content has a defined, non-degenerate accessible
/// presentation, and this file is the mechanical half of that claim: that each kind produces a
/// named control a keyboard can reach, that a table is a real grid with real headers, and that a
/// part humcp will not open says so instead of offering a button that does nothing. The other half
/// — whether any of it is pleasant — only a screen reader can answer, and that pass is in
/// phase4-notes.
/// </remarks>
[Collection(AppCollection.Name)]
public class ResultTests(AppFixture fixture, ITestOutputHelper output)
{
    private Window Main => fixture.Window;

    private AutomationElement Results =>
        Main.FindFirstDescendant(cf => cf.ByAutomationId("MainResults"))
        ?? throw new InvalidOperationException("The result view is not in the UIA tree.");

    // ------------------------------------------------------------------ every kind of part

    [Fact]
    public void Every_kind_of_part_gets_a_control_with_a_name_on_it()
    {
        Run("One of everything", "One of everything");

        var named = Focusables().Select(e => UiaTree.Safe(() => e.Name) ?? "").ToList();
        output.WriteLine(string.Join(" | ", named));

        // §7.1, over parts nobody wrote a template for by hand: text, a picture, a sound, a link,
        // an embedded resource and a blob, each reachable and each announcing what it is.
        Assert.DoesNotContain("", named);

        Assert.Contains(named, n => n.StartsWith("Text, part 1 of 6", StringComparison.Ordinal));
        Assert.Contains(named, n => n.StartsWith("Save Image, PNG,", StringComparison.Ordinal));
        Assert.Contains(named, n => n.StartsWith("Save Audio, WAV,", StringComparison.Ordinal));
        Assert.Contains(named, n => n == "Resource link, The MCP specification");
        Assert.Contains(named, n => n.StartsWith("Embedded resource, humcp://demo/notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void The_result_is_a_group_of_parts_rather_than_a_list_of_data_items()
    {
        Run("One of everything", "One of everything");

        // §7.12 again, on the way back: an untouched ItemsControl would report itself as a list and
        // every part as a DataItem named after a C# class.
        Assert.Equal(ControlType.Group, Results.ControlType);
        Assert.DoesNotContain(UiaTree.Descendants(Results),
            e => UiaTree.Safe(() => e.ControlType) == ControlType.DataItem);

        // And the view is not a tab stop of its own (§7.13).
        Assert.False(Results.Properties.IsKeyboardFocusable.ValueOrDefault);
    }

    [Fact]
    public void An_image_can_be_saved_and_opened_and_an_unknown_file_only_saved()
    {
        Run("One of everything", "One of everything");

        var names = Focusables().Select(e => UiaTree.Safe(() => e.Name) ?? "").ToList();

        // A PNG is a type humcp recognises, so the extension it would write is one we chose.
        Assert.Contains(names, n => n.StartsWith("Open Image, PNG,", StringComparison.Ordinal));

        // The blob's media type is not on the list, so it is saved as .bin and never handed to the
        // shell — "open" on an unrecognised type is "run whatever this turns out to be".
        Assert.Contains(names, n => n.StartsWith("Save File,", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Open File,", StringComparison.Ordinal));

        // §7.20 and §7.24 together: in words, in a control the keyboard reaches, rather than by
        // the absence of a button or by a label nothing speaks.
        var blob = Focusables().First(e =>
            (UiaTree.Safe(() => e.Name) ?? "").StartsWith("File, X-NOT-A-REAL-TYPE", StringComparison.Ordinal));

        Assert.Contains("does not recognise this type", blob.AsTextBox().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_humcp_will_not_follow_has_no_button_and_says_why()
    {
        Run("Three resource links", "Three resource links");

        var names = Focusables().Select(e => UiaTree.Safe(() => e.Name) ?? "").ToList();
        output.WriteLine(string.Join(" | ", names));

        // One web address, one file, one scheme humcp does not follow — and every one of the three
        // can still be copied.
        Assert.Single(names, n => n == "Open in browser");
        Assert.Single(names, n => n == "Show in folder");
        Assert.Equal(3, names.Count(n => n == "Copy link"));

        // Phase 13: what is not a web address can be read through the server that sent it, which
        // never involves the shell — so the file and the custom scheme have Read resource, and the
        // web page, which the browser opens, does not.
        Assert.Equal(2, names.Count(n => n == "Read resource"));

        var inert = Focusables().Single(e => UiaTree.Safe(() => e.Name) == "Resource link, A scheme humcp does not follow");
        Assert.Contains("will not hand this address to Windows", UiaTree.Safe(() => inert.HelpText) ?? "");
    }

    // ------------------------------------------------------------------ structured content

    [Fact]
    public void A_structured_result_that_repeats_the_text_is_shown_once_and_said_once()
    {
        Run("Text in a wrapper", "Text in a wrapper");

        // The filesystem server's shape, and 13 of the corpus's 25 output schemas: the same string
        // as text and as structured content. Two panes of it would be Phase 1's "everything spoken
        // twice" in a new place (phase1-notes 2.7).
        var edits = UiaTree.Descendants(Results)
            .Where(e => UiaTree.Safe(() => e.ControlType) == ControlType.Edit).ToList();

        Assert.Single(edits);
        Assert.DoesNotContain(UiaTree.Descendants(Results), e => UiaTree.Safe(() => e.ControlType) == ControlType.Tree);

        // Not silently, though: the announcement says which of the two it is showing (§7.18).
        Assert.Contains("repeats the text", Status());
    }

    [Fact]
    public void A_table_is_a_grid_with_headers_and_every_row_in_the_automation_tree()
    {
        using var form = OpenForm("Table of entities", "Table of entities — humcp test server");
        form.Run();

        var grid = WaitFor(
            () => form.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid)),
            "a grid in the form's result pane");

        var rows = grid.FindAllChildren(cf => cf.ByControlType(ControlType.DataItem));
        output.WriteLine($"{grid.Name} — {rows.Length} rows");

        // §7.6, applied to a grid: row and column virtualization are both off, so every row the
        // grid says it is showing is a row a screen reader can reach. With virtualization on, this
        // is the assertion that would count a viewport instead of a table.
        Assert.Equal(8, rows.Length);

        // The name carries the shape, so it is heard on arrival rather than counted by hand.
        Assert.Equal("Entities, table, 8 rows, 5 columns", grid.Name);

        var headers = grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.Header));
        Assert.NotNull(headers);
        Assert.Contains(UiaTree.Children(headers), h => UiaTree.Safe(() => h.Name) == "Observations");
    }

    [Fact]
    public void Json_sent_as_text_is_shown_as_the_structure_it_is()
    {
        // humcp 1.1. Open-Meteo's shape: no structured content, the whole forecast as one line of
        // JSON in a text part. 1.0 put that line in a text box, where the 3rd hour and the 3rd
        // temperature were 300 characters apart and nothing said they belonged together.
        using var form = OpenForm("Forecast as JSON text", "Forecast as JSON text — humcp test server");
        form.Run();

        var tree = WaitFor(
            () => UiaTree.Descendants(form.Window)
                .FirstOrDefault(e => UiaTree.Safe(() => e.ControlType) == ControlType.Tree),
            "a result tree in the form's result pane");

        var names = UiaTree.Descendants(tree).Select(e => UiaTree.Safe(() => e.Name) ?? "").ToList();
        output.WriteLine(string.Join(" | ", names));

        Assert.Contains("Hourly, table, 6 rows, 2 columns", names);

        // Shown once: no text box holding the same JSON beside it.
        Assert.DoesNotContain(UiaTree.Descendants(form.Window), e =>
            UiaTree.Safe(() => e.ControlType) == ControlType.Edit
            && (UiaTree.Safe(() => e.AsTextBox().Text) ?? "").StartsWith("{\"latitude\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_inside_a_structure_opens_as_a_grid_of_its_own()
    {
        Run("Awkward structure", "Awkward structure");

        var tree = WaitFor(
            () => UiaTree.Descendants(Results)
                .FirstOrDefault(e => UiaTree.Safe(() => e.ControlType) == ControlType.Tree),
            "a tree in the result pane");

        var node = UiaTree.Descendants(tree)
            .FirstOrDefault(e => (UiaTree.Safe(() => e.Name) ?? "").StartsWith("Rows, table", StringComparison.Ordinal));

        Assert.True(node is not null,
            "The structure's table is not in the tree: " +
            string.Join(" | ", UiaTree.Descendants(tree).Select(e => UiaTree.Safe(() => e.Name))));

        // The node says the real size before it is opened — two thousand rows, of which the grid
        // will show the two hundred it can (§7.18: a limit that bites says so).
        Assert.Contains("2,000 rows", node!.Name);

        Input.EnsureForeground(Main, fixture.Automation);
        node.Patterns.SelectionItem.Pattern.Select();
        node.Focus();
        Input.Settle(200);
        Input.Press(VirtualKeyShort.RETURN);
        Input.Settle(600);

        var grid = fixture.Owned("Rows — table, 2,000 rows, 2 columns", TimeSpan.FromSeconds(10));

        Assert.True(grid is not null,
            "Enter on a table node opened no grid window. humcp has: " + string.Join(" | ", fixture.Titles()));

        try
        {
            var opened = grid!.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid))!;

            Assert.Equal(200, opened.FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)).Length);

            // A grid focused in code starts with no current cell, and a DataGrid with no current
            // cell does not handle an arrow key at all — so WPF took the press as directional
            // navigation and moved focus out of the grid into the window's menu bar, with two
            // hundred rows on screen and none of them reachable (phase4-notes 4.3).
            Assert.NotEmpty(opened.Patterns.Selection.Pattern.Selection.Value);

            Input.Press(VirtualKeyShort.DOWN);
            Input.Settle(300);

            var focused = fixture.Automation.FocusedElement();
            output.WriteLine(UiaTree.Safe(() => focused?.Name) ?? "(nothing)");

            Assert.NotEqual(ControlType.MenuItem, UiaTree.Safe(() => focused?.ControlType));
        }
        finally
        {
            grid!.Close();
            Input.Settle(200);
        }
    }

    [Fact]
    public void A_result_with_nothing_in_it_still_says_what_happened()
    {
        Run("Nothing at all", "Nothing at all");

        var box = Focusables().First();

        Assert.Equal("Result", UiaTree.Safe(() => box.Name));
        Assert.Equal("The tool returned no content.", box.AsTextBox().Text);
    }

    [Fact]
    public void F1_in_the_result_writes_the_whole_thing_out_including_what_is_not_on_screen()
    {
        Run("One of everything", "One of everything");

        Input.EnsureForeground(Main, fixture.Automation);
        Focusables().First().Focus();
        Input.Settle(200);
        Input.Press(VirtualKeyShort.F1);
        Input.Settle(500);

        var details = fixture.Owned("Details: One of everything", TimeSpan.FromSeconds(10));
        Assert.True(details is not null, "F1 in the result pane opened no details window.");

        try
        {
            var body = details!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text ?? "";
            output.WriteLine(UiaTree.Flatten(body, 300));

            // The rendered result is the version that is easy to read; this is the version that is
            // complete, and it is one keystroke away from it.
            Assert.Contains("Part 1 of 6", body);
            Assert.Contains("https://modelcontextprotocol.io/specification", body);
            Assert.Contains("Image, PNG,", body);
        }
        finally
        {
            details!.Close();
            Input.Settle(200);
        }
    }

    // ------------------------------------------------------------------ what the first pass heard

    [Fact]
    public void A_grid_row_announces_its_values_and_not_a_class_name()
    {
        using var form = OpenForm("Table of entities", "Table of entities — humcp test server");
        form.Run();

        var grid = WaitFor(
            () => form.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid)),
            "a grid in the form's result pane");

        var row = grid.FindFirstChild(cf => cf.ByControlType(ControlType.DataItem));
        Assert.NotNull(row);
        output.WriteLine(row.Name);

        // §7.23 for a third control. The first Phase 4 pass heard "ResultRow { Number = 1, Cells =
        // System.Collections.Generic.List`1[Humcp.Schema.ResultCell], Summary = …" on every row.
        Assert.DoesNotContain("ResultRow", row.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections", row.Name, StringComparison.Ordinal);
        Assert.StartsWith("entity-0001", row.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_offers_a_description_and_says_where_to_get_the_model_without_one()
    {
        // Phase 10. The suite's model store is empty (AppFixture), which is every first run: the
        // button is there anyway, so the feature can be found, and pressing it says how to get one.
        Run("One of everything", "One of everything");

        var describe = Focusables().FirstOrDefault(e =>
            (UiaTree.Safe(() => e.Name) ?? "").StartsWith("Describe Image, PNG,", StringComparison.Ordinal));
        Assert.True(describe is not null, "No Describe button: " + string.Join(" | ", Focusables().Select(e => UiaTree.Safe(() => e.Name))));
        Assert.Equal("Control plus D", describe!.Properties.AcceleratorKey.ValueOrDefault);

        describe.AsButton().Invoke();

        var status = Main.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !(UiaTree.Safe(() => status!.Name) ?? "").Contains("File, Local model", StringComparison.Ordinal))
            Thread.Sleep(150);

        Assert.Contains("File, Local model", UiaTree.Safe(() => status!.Name) ?? "");

        var box = Focusables().First(e => (UiaTree.Safe(() => e.Name) ?? "").StartsWith("Image, PNG,", StringComparison.Ordinal));
        Assert.Contains("File, Local model", box.AsTextBox().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_result_offers_a_summary_and_a_short_one_does_not()
    {
        // Phase 11. Two hundred lines is long enough to want one; the suite has no model, so the offer
        // says where to get one, and the button says it again rather than doing nothing.
        Run("Two hundred lines", "Two hundred lines");

        var summary = Focusables().FirstOrDefault(e => UiaTree.Safe(() => e.Name) == "Summary, by the local model");
        Assert.True(summary is not null, "No summary part: " + string.Join(" | ", Focusables().Select(e => UiaTree.Safe(() => e.Name))));
        Assert.Contains("File, Local model", summary!.AsTextBox().Text, StringComparison.Ordinal);

        var button = Focusables().First(e => UiaTree.Safe(() => e.Name) == "Summarise the result");
        Assert.Equal("Control plus M", button.Properties.AcceleratorKey.ValueOrDefault);

        button.AsButton().Invoke();
        WaitFor(() => Status().Contains("File, Local model", StringComparison.Ordinal) ? Status() : null, "the summarise answer");

        Run("Server information", "Server information");
        Assert.DoesNotContain(Focusables(), e => UiaTree.Safe(() => e.Name) == "Summary, by the local model");
    }

    [Fact]
    public void A_picture_says_what_it_is_somewhere_a_reader_will_reach()
    {
        Run("One of everything", "One of everything");

        var box = Focusables().FirstOrDefault(e =>
            (UiaTree.Safe(() => e.Name) ?? "").StartsWith("Image, PNG,", StringComparison.Ordinal));

        // §7.24: the part heading and the "there is no description of it" sentence were TextBlocks
        // for the length of one pass — on screen, in nobody's ear, with the Save button the only
        // thing Tab ever landed on (phase4-notes 4.4).
        Assert.True(box is not null,
            "Nothing focusable in the image part says what it is: " +
            string.Join(" | ", Focusables().Select(e => UiaTree.Safe(() => e.Name))));

        Assert.Equal(ControlType.Edit, box!.ControlType);
        Assert.Contains("no description of it", box.AsTextBox().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_on_a_button_is_not_announced_as_its_keyboard_shortcut()
    {
        Run("One of everything", "One of everything");

        var save = Focusables().First(e =>
            (UiaTree.Safe(() => e.Name) ?? "").StartsWith("Save Image,", StringComparison.Ordinal));

        // WPF falls back to a bound RoutedUICommand's Text for a button's AcceleratorKey, so a
        // command called "Save as" made every button bound to it announce "Save as" in the slot a
        // screen reader reads out as the keyboard shortcut (§7.27).
        Assert.Equal("", UiaTree.Safe(() => save.Properties.AcceleratorKey.ValueOrDefault) ?? "");
    }

    [Fact]
    public void A_link_reads_as_sentences_rather_than_as_a_string_join()
    {
        Run("Three resource links", "Three resource links");

        var link = Focusables().First(e => UiaTree.Safe(() => e.Name) == "Resource link, A web page");
        var help = UiaTree.Safe(() => link.HelpText) ?? "";

        output.WriteLine(help);

        Assert.DoesNotContain("..", help, StringComparison.Ordinal);
        Assert.Contains("Opens this address in your browser.", help, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_value_opens_as_the_same_tree_it_came_from()
    {
        Run("Awkward structure", "Awkward structure");

        var tree = WaitFor(
            () => UiaTree.Descendants(Results)
                .FirstOrDefault(e => UiaTree.Safe(() => e.ControlType) == ControlType.Tree),
            "a tree in the result pane");

        var node = UiaTree.Descendants(tree)
            .First(e => (UiaTree.Safe(() => e.Name) ?? "").StartsWith("Nest,", StringComparison.Ordinal));

        Input.EnsureForeground(Main, fixture.Automation);
        node.Patterns.SelectionItem.Pattern.Select();
        node.Focus();
        Input.Settle(200);
        Input.Press(VirtualKeyShort.RETURN);
        Input.Settle(600);

        var window = fixture.Owned("Nest — 2 fields", TimeSpan.FromSeconds(10));

        // Not JSON in a text box, which is what it was until the first pass met it (phase4-notes
        // 4.7): the value had a tree a moment earlier and losing it on the way into a window is a
        // step down, not a presentation.
        Assert.True(window is not null,
            "Enter on a nested object opened no tree window. humcp has: " + string.Join(" | ", fixture.Titles()));

        try
        {
            var nested = window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
            Assert.NotNull(nested);
            Assert.Equal("Nest, 2 fields", nested.Name);
        }
        finally
        {
            window!.Close();
            Input.Settle(200);
        }
    }

    [Fact]
    public void The_completion_announcement_reads_as_one_sentence()
    {
        Run("One of everything", "One of everything");

        var status = Status();
        output.WriteLine(status);

        // Every part in the same register. The first pass heard "1 line of text, Image, PNG, 70
        // bytes, a link to The MCP specification" — three grammars in one sentence.
        Assert.Contains("an image, PNG,", status, StringComparison.Ordinal);
        Assert.Contains("a sound, WAV,", status, StringComparison.Ordinal);
        Assert.Contains("a link to The MCP specification", status, StringComparison.Ordinal);
        Assert.Contains("a file,", status, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Runs a zero-argument tool the way the Run button does. Setup goes through UI Automation
    /// rather than the keyboard: <c>Invoke</c> needs no foreground, and getting a result on screen
    /// is setup for almost everything here (§9).
    /// </summary>
    private void Run(string toolLabel, string displayName)
    {
        Select(toolLabel);

        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))?.AsButton();
        Assert.True(run is not null, "The main window has no Run button.");

        WaitUntilEnabled(run!);
        run!.Invoke();

        WaitFor(
            () => Heading() == $"Result — {displayName}" || Heading() == $"Result — {displayName} — error"
                ? Heading()
                : null,
            $"the result heading to say {displayName}");

        Input.Settle(250);
    }

    private OpenFormWindow OpenForm(string toolLabel, string windowTitle)
    {
        if (fixture.Owned(windowTitle, TimeSpan.FromMilliseconds(200)) is { } already)
            return new OpenFormWindow(already);

        Select(toolLabel);

        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))?.AsButton();
        WaitUntilEnabled(run!);
        run!.Invoke();

        var window = fixture.Owned(windowTitle, TimeSpan.FromSeconds(10));
        Assert.True(window is not null, $"The form window {windowTitle} did not open.");

        Input.Settle(300);
        return new OpenFormWindow(window!);
    }

    private void Select(string toolLabel)
    {
        var tree = Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Assert.NotNull(tree);

        var tool = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith(toolLabel, StringComparison.Ordinal));

        Assert.True(tool is not null, $"No tool called {toolLabel} in the tree.");

        tool!.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(150);
    }

    /// <summary>Everything in the result a keyboard can reach, in tab order.</summary>
    private List<AutomationElement> Focusables() =>
        [.. UiaTree.Descendants(Results)
            .Where(e => UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)];

    private string Heading() =>
        Main.FindFirstDescendant(cf => cf.ByAutomationId("ResultHeading"))?.Name ?? "";

    private string Status() =>
        Main.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"))?.Name ?? "";

    /// <summary>Waits for a state rather than for a duration (§9): the machine is not always ours.</summary>
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

    private static void WaitUntilEnabled(Button button)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (UiaTree.Safe(() => button.IsEnabled) == true) return;
            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The Run button never became enabled.");
    }

    /// <summary>A form window, closed when the test finishes with it.</summary>
    private sealed class OpenFormWindow(Window window) : IDisposable
    {
        public Window Window { get; } = window;

        public void Run()
        {
            Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))?.AsButton()?.Invoke();
            Input.Settle(600);
        }

        public void Dispose()
        {
            try { Window.Close(); } catch { /* the test may already have closed it */ }
            Input.Settle(200);
        }
    }
}

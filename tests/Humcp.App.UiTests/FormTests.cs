using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// The generated form, asserted against the live UIA tree of a running application (ROADMAP §9).
/// </summary>
/// <remarks>
/// Everything here is a rule from §7 that generated WPF violates by default, and every one of them
/// was a defect in the Phase 0 spike before it was a test (phase0-findings §7.3, checks 5–9). None
/// of it proves the form is pleasant to use — only NVDA does that — but all of it fails the moment
/// somebody makes the form a <c>List</c> of <c>DataItem</c>s again.
/// </remarks>
[Collection(AppCollection.Name)]
public class FormTests(AppFixture fixture, ITestOutputHelper output)
{
    private const string SearchForm = "Search documents — humcp test server";
    private const string WideForm = "Forty parameters — humcp test server";

    private Window Main => fixture.Window;

    // ------------------------------------------------------------------ opening

    [Fact]
    public void A_tool_with_parameters_opens_a_form_window_from_the_keyboard()
    {
        Input.EnsureForeground(Main, fixture.Automation);

        var tool = Select("Search documents");
        tool.Focus();
        Input.Settle(150);
        Input.Press(VirtualKeyShort.RETURN);

        using var form = new OpenForm(
            fixture.Owned(SearchForm, TimeSpan.FromSeconds(10))
            ?? throw new InvalidOperationException("Enter on a tool with parameters opened no form."));

        Assert.Equal(SearchForm, form.Window.Title);

        // §7.17: a WPF Window announces its whole content as its name unless told otherwise, which
        // on this window would be every label on the form.
        Assert.Equal(SearchForm, form.Window.Name);
    }

    [Fact]
    public void The_form_is_a_group_of_groups_rather_than_a_list_of_data_items()
    {
        using var form = Open("Search documents", SearchForm);

        var fields = form.Fields;

        // §7.12: untouched, an ItemsControl reports itself as a List and each field as a DataItem
        // named after the bound object's ToString() — "list, 8 items", then a C# class name eight
        // times. This is the assertion that catches that coming back.
        Assert.Equal(ControlType.Group, fields.ControlType);
        Assert.DoesNotContain(UiaTree.Descendants(fields),
            e => UiaTree.Safe(() => e.ControlType) == ControlType.DataItem);

        // §7.13: and the container is not a tab stop of its own.
        Assert.False(fields.Properties.IsKeyboardFocusable.ValueOrDefault);
    }

    [Fact]
    public void Optional_parameters_start_hidden_and_the_toggle_brings_them_back()
    {
        using var form = Open("Search documents", SearchForm);

        // Two required of fifteen. The corpus is why this is the default: 491 of its 785 top-level
        // parameters are optional, and its largest form is 23 fields with one required.
        Assert.NotNull(form.Control("Query"));
        Assert.NotNull(form.Control("Scope"));
        Assert.Null(form.Control("Limit"));
        Assert.Null(form.Control("Api token"));

        form.ShowOptional();

        Assert.NotNull(form.Control("Limit"));
        Assert.NotNull(form.Control("Api token"));
        Assert.NotNull(form.Control("Notes"));
    }

    [Fact]
    public void Every_control_on_the_form_has_a_name_and_none_of_them_says_required()
    {
        using var form = Open("Search documents", SearchForm);
        form.ShowOptional();

        var unnamed = new List<string>();
        var decorated = new List<string>();
        var required = 0;

        foreach (var element in UiaTree.Descendants(form.Fields))
        {
            if (UiaTree.Safe(() => element.Properties.IsKeyboardFocusable.ValueOrDefault) != true) continue;

            var name = UiaTree.Safe(() => element.Name) ?? "";

            if (string.IsNullOrWhiteSpace(name)) unnamed.Add(UiaTree.Safe(() => element.ControlType).ToString() ?? "?");
            if (name.Contains("required", StringComparison.OrdinalIgnoreCase)) decorated.Add(name);
            if (UiaTree.Safe(() => element.Properties.IsRequiredForForm.ValueOrDefault) == true) required++;
        }

        Assert.True(unnamed.Count == 0,
            $"Unnamed and focusable: {string.Join(", ", unnamed)}. The form holds: {form.Names()}");

        // §7.4: "required" is a property, not a suffix. Appending it to the name takes the choice
        // of how to say it away from the screen reader and from the person configuring it.
        Assert.Empty(decorated);
        Assert.Equal(2, required);
    }

    [Fact]
    public void Every_input_carries_help_text_or_a_label()
    {
        using var form = Open("Search documents", SearchForm);
        form.ShowOptional();

        var bare = new List<string>();

        foreach (var element in UiaTree.Descendants(form.Fields))
        {
            if (UiaTree.Safe(() => element.ControlType) is not
                (ControlType.Edit or ControlType.ComboBox or ControlType.CheckBox)) continue;

            var help = UiaTree.Safe(() => element.HelpText) ?? "";
            var labelledBy = UiaTree.Safe(() => element.Properties.LabeledBy.ValueOrDefault);

            if (string.IsNullOrWhiteSpace(help) && labelledBy is null)
                bare.Add(UiaTree.Safe(() => element.Name) ?? "(unnamed)");
        }

        Assert.Empty(bare);
    }

    [Fact]
    public void A_control_with_a_keystroke_nothing_announces_says_so_in_its_help_text()
    {
        using var form = Open("Search documents", SearchForm);
        form.ShowOptional();

        // §7.15: a DatePicker opens its calendar with Alt+Down Arrow and no screen reader mentions
        // it. Spelled out in words, because they differ on whether they announce a plus sign.
        var date = form.Control("Since");
        Assert.True(date is not null, "No focusable control called Since. The form holds: " + form.Names());

        var help = UiaTree.Descendants(date)
            .Select(e => UiaTree.Safe(() => e.HelpText) ?? "")
            .Append(UiaTree.Safe(() => date.HelpText) ?? "")
            .FirstOrDefault(h => h.Contains("Alt plus Down Arrow", StringComparison.Ordinal));

        Assert.NotNull(help);
    }

    [Fact]
    public void A_choice_announces_its_options_by_their_labels()
    {
        // Found by ear in the first manual pass, in all three screen readers: every option was read
        // as "ChoiceOption { Label = Titles, Value = …". An item's accessible name is the plain text
        // of its content, and with a bound object that means its ToString — the same trap as §7.12's
        // DataItem names, one layer down (phase3-notes 4.1).
        using var form = Open("Search documents", SearchForm);

        var scope = form.Control("Scope")?.AsComboBox();
        Assert.NotNull(scope);

        var names = scope.Items.Select(i => UiaTree.Safe(() => i.Name) ?? "").ToList();
        foreach (var name in names) output.WriteLine(name);

        Assert.Equal(["Not set", "Everything", "Titles", "Bodies", "Comments"], names);
        Assert.DoesNotContain(names, n => n.Contains('{'));
    }

    [Fact]
    public void Showing_the_optional_parameters_keeps_the_caret_where_it_was()
    {
        // Revealing them rebuilds the list the form is bound to, which destroys the control the
        // user is standing in. Before this, Ctrl+O from the Query box threw focus out to the
        // window, where a screen reader says the window's title and nothing else (phase3-notes 4.4).
        using var form = Open("Search documents", SearchForm);

        Input.EnsureForeground(form.Window, fixture.Automation);
        form.Control("Query")!.Focus();
        Input.Settle(200);

        Input.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_O);
        Input.Settle(600);

        var focused = fixture.Automation.FocusedElement();
        Assert.Equal("Query", UiaTree.Safe(() => focused?.Name));
    }

    [Fact]
    public void A_form_whose_first_field_is_a_group_still_puts_the_caret_in_a_box()
    {
        // A GroupBox is deliberately not a tab stop (§7.13), so a form that opens on one had
        // nowhere to send the caret and left it on the window (phase3-notes 4.5). The group's first
        // member is where the user was going anyway.
        using var form = Open("Walk a tree", "Walk a tree — humcp test server");

        var focused = fixture.Automation.FocusedElement();
        output.WriteLine(UiaTree.Safe(() => focused?.Name) ?? "(nothing)");

        Assert.Equal("Name", UiaTree.Safe(() => focused?.Name));
    }

    [Fact]
    public void Control_plus_Enter_submits_even_when_a_button_has_the_caret()
    {
        // A WPF Button handles Enter itself and does not look at the modifiers, so Ctrl+Enter on
        // one of a list's own buttons pressed the button — which on an object list silently added
        // another empty row instead of running the tool (phase3-notes 4.7).
        using var form = Open("Search documents", SearchForm);

        Input.EnsureForeground(form.Window, fixture.Automation);

        var run = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"));
        run!.Focus();
        Input.Settle(200);

        Input.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);
        Input.Settle(800);

        // It submitted: the form is incomplete, so the problems pane is showing.
        var problems = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("Problems"));
        Assert.NotNull(problems);
        Assert.NotEmpty(problems.FindAllChildren());
    }

    [Fact]
    public void A_fields_problem_is_in_the_help_text_a_reader_speaks()
    {
        // NVDA does not speak ItemStatus on focus, so the field's own copy of the message was set,
        // correct and silent (phase3-notes 4.9). Both channels now carry it.
        using var form = Open("Search documents", SearchForm);

        form.Run();

        var query = form.Control("Query");
        Assert.NotNull(query);

        Assert.Contains("required", UiaTree.Safe(() => query.Properties.ItemStatus.ValueOrDefault) ?? "",
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Problem: This one is required.", UiaTree.Safe(() => query.HelpText) ?? "");
    }

    [Fact]
    public void The_entries_of_a_list_can_be_removed_with_the_key_the_help_text_names()
    {
        // Focus moves into a list box's first item without selecting it, and a one-item list gives
        // the arrows nowhere to go — so Delete stayed disabled however long you pressed it, on a
        // list whose help text says Delete removes the selected one (phase3-notes 4.8).
        using var form = Open("Search documents", SearchForm);
        form.ShowOptional();

        Input.EnsureForeground(form.Window, fixture.Automation);

        var tags = form.Control("Tags");
        Assert.NotNull(tags);

        tags.Focus();
        Input.Settle(200);
        Input.Type("urgent");

        var entries = UiaTree.Descendants(form.Fields)
            .FirstOrDefault(e => UiaTree.Safe(() => e.Name) == "Tags, entries added");
        Assert.True(entries is not null, "No entries list for Tags. The form holds: " + form.Names());

        var add = UiaTree.Descendants(form.Fields)
            .FirstOrDefault(e => UiaTree.Safe(() => e.Name) == "Add to Tags");
        Assert.True(add is not null, "No Add button for Tags.");

        // Its enabled state follows the entry box through a command re-query, which is a dispatcher
        // hop behind the text landing there.
        WaitUntilEnabled(add!, "Add to Tags");
        add!.AsButton().Invoke();
        Input.Settle(400);

        Assert.Single(entries!.FindAllChildren());

        // The whole point: focus the list, press Delete, and the entry goes — with no Space first.
        entries.Focus();
        Input.Settle(300);
        Input.Press(VirtualKeyShort.DELETE);
        Input.Settle(500);

        Assert.Empty(entries.FindAllChildren());
    }

    // ------------------------------------------------------------------ windows

    [Fact]
    public void The_close_button_closes_the_window()
    {
        // It did not, for a whole manual pass: IsCancel closes a *modal* dialog and does nothing at
        // all on a window shown with Show() (phase3-notes 4.2).
        var form = Open("Search documents", SearchForm);

        var close = form.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => UiaTree.Safe(() => b.Name) == "Close");

        Assert.True(close is not null, "The form has no Close button.");

        close!.AsButton().Invoke();
        Input.Settle(400);

        Assert.Null(fixture.Owned(SearchForm, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Escape_closes_a_form_window()
    {
        var form = Open("Search documents", SearchForm);

        Input.EnsureForeground(form.Window, fixture.Automation);
        form.FocusFirstField();

        Input.Press(VirtualKeyShort.ESCAPE);
        Input.Settle(400);

        Assert.Null(fixture.Owned(SearchForm, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Every_window_lists_every_other_window_in_its_window_menu()
    {
        // §8's window model, brought forward from Phase 6 after the first manual pass: the forms
        // "read like dialogs rather than windows", and there was no way from one to another.
        using var form = Open("Search documents", SearchForm);

        // By name. A window's title bar carries a MenuBar of its own — the system menu — and it is
        // the one a search by control type finds first; ours reports as a Menu rather than a
        // MenuBar in any case.
        var menu = form.Window.FindFirstDescendant(cf => cf.ByName("Main menu"));
        Assert.True(menu is not null, "The form window has no menu bar.");
        Assert.Equal(ControlType.Menu, menu!.ControlType);

        var items = UiaTree.Descendants(menu!)
            .Select(e => (Element: e, Name: UiaTree.Safe(() => e.Name) ?? "",
                          Type: UiaTree.Safe(() => e.ControlType)))
            .ToList();

        foreach (var item in items) output.WriteLine($"{item.Type}:{item.Name}");

        var windowMenu = items.FirstOrDefault(i => i.Name == "Window").Element;

        Assert.True(windowMenu is not null,
            "The menu bar has no Window menu. It holds: " +
            string.Join(" | ", items.Select(i => $"{i.Type}:{i.Name}")));

        windowMenu!.Patterns.ExpandCollapse.Pattern.Expand();
        Input.Settle(300);

        var entries = UiaTree.Descendants(windowMenu)
            .Where(e => UiaTree.Safe(() => e.ControlType) == ControlType.MenuItem)
            .Select(e => UiaTree.Safe(() => e.Name) ?? "")
            .ToList();

        foreach (var entry in entries) output.WriteLine(entry);

        Assert.Contains(entries, e => e.StartsWith("1 humcp", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("Search documents", StringComparison.Ordinal));

        // Which one you are in has to be a state a reader can read, not a mark on the screen: with
        // IsChecked alone WPF drew the tick and exposed no toggle state at all (phase3-notes 4.2).
        var current = UiaTree.Descendants(windowMenu)
            .FirstOrDefault(e => (UiaTree.Safe(() => e.Name) ?? "").Contains("Search documents", StringComparison.Ordinal));

        Assert.NotNull(current);
        Assert.Equal(ToggleState.On, current.Patterns.Toggle.Pattern.ToggleState.Value);

        windowMenu.Patterns.ExpandCollapse.Pattern.Collapse();
        Input.Settle(200);
    }

    // ------------------------------------------------------------------ tab order

    [Fact]
    public void The_forms_tab_order_reaches_the_controls_and_contains_nothing_else()
    {
        using var form = Open("Search documents", SearchForm);

        Input.EnsureForeground(form.Window, fixture.Automation);
        form.FocusFirstField();

        var stops = Input.WalkTabOrder(form.Window, fixture.Automation, out var cycled);
        foreach (var stop in stops) output.WriteLine(stop);

        Assert.Contains(stops, s => s == "Edit:Query");
        Assert.Contains(stops, s => s == "ComboBox:Scope");
        Assert.Contains(stops, s => s == "CheckBox:Show optional parameters");
        Assert.Contains(stops, s => s == "Button:Run tool");
        Assert.Contains(stops, s => s.StartsWith("Edit:Result", StringComparison.Ordinal));

        // The other direction of the rule (§7.14): no scroll viewer, no unnamed group, no splitter,
        // and no menu bar — a menu is reached with Alt and arrowed between, never tabbed to.
        Assert.DoesNotContain(stops, s => s.StartsWith("Pane:", StringComparison.Ordinal));
        Assert.DoesNotContain(stops, s => s.StartsWith("Thumb:", StringComparison.Ordinal));
        Assert.DoesNotContain(stops, s => s.StartsWith("MenuItem:", StringComparison.Ordinal));
        Assert.DoesNotContain(stops, s => s == "Group:");

        Assert.True(cycled, "The tab order never returned to its first stop.");
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Submitting_an_incomplete_form_lists_the_problems_and_marks_the_fields()
    {
        using var form = Open("Search documents", SearchForm);

        form.Run();

        // The error summary (§7.5's second half): a list you can arrow through, because a control
        // that says what is wrong with it only says so once you have found it.
        var problems = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("Problems"));
        Assert.NotNull(problems);

        var entries = problems.FindAllChildren();
        Assert.NotEmpty(entries);
        Assert.Contains(entries,
            e => (UiaTree.Safe(() => e.Name) ?? "").Contains("required", StringComparison.OrdinalIgnoreCase));

        // §7.5: and the field carries the message itself, because WPF's red border is not a message.
        var query = form.Control("Query");
        Assert.NotNull(query);
        Assert.Contains("required", UiaTree.Safe(() => query.Properties.ItemStatus.ValueOrDefault) ?? "",
            StringComparison.OrdinalIgnoreCase);

        // Never a veto (§5): the tool can still be run, and running it again does run it.
        var run = form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"));
        Assert.True(run!.Properties.IsEnabled);
    }

    [Fact]
    public void Control_plus_Enter_submits_and_the_caret_lands_on_the_first_problem()
    {
        using var form = Open("Search documents", SearchForm);

        Input.EnsureForeground(form.Window, fixture.Automation);
        form.FocusFirstField();

        Input.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);
        Input.Settle(500);

        // §7.8: focus moves to the first invalid field — first in tab order, not first alphabetically.
        // The validator reports Scope before Query, because it sorts by path within a depth.
        var focused = fixture.Automation.FocusedElement();
        Assert.Equal("Query", UiaTree.Safe(() => focused?.Name));
    }

    // ------------------------------------------------------------------ history and sub-forms

    [Fact]
    public void A_value_used_once_is_offered_the_next_time_the_form_opens()
    {
        // ROADMAP §4, pitfall 3: MCP has no way to complete a tool argument — completion/complete
        // takes ref/prompt and ref/resource and there is no ref/tool — so the only source of
        // suggestions is what this user typed last time.
        const string Echo = "Echo — humcp test server";

        using (var form = Open("Echo", Echo))
        {
            var text = form.Control("Text");
            Assert.NotNull(text);
            Assert.Equal(ControlType.Edit, text.ControlType);

            text.AsTextBox().Text = "chartreuse";
            form.Run();

            Assert.Contains("chartreuse", form.Result());
        }

        using (var reopened = Open("Echo", Echo))
        {
            // The control itself changed: a parameter with values behind it is a combo box whose
            // drop-down can be arrowed through, and one without stays a plain box.
            var text = reopened.Control("Text");
            Assert.NotNull(text);
            Assert.Equal(ControlType.ComboBox, text.ControlType);

            Assert.Contains("Alt plus Down Arrow", text.HelpText);
            Assert.Contains("chartreuse", text.AsComboBox().Items.Select(i => i.Name));
        }
    }

    [Fact]
    public void A_row_of_a_list_of_objects_opens_as_a_form_of_its_own()
    {
        // §5, Tier 2. A window rather than a third level of nesting: a form inside a row inside a
        // form is not a thing anyone can keep their place in by ear.
        using var form = Open("Search documents", SearchForm);
        form.ShowOptional();

        var add = UiaTree.Descendants(form.Fields)
            .FirstOrDefault(e => UiaTree.Safe(() => e.Name) == "Add an entry to Rules");

        Assert.True(add is not null, "No button to add a rule. The form holds: " + form.Names());

        add!.AsButton().Invoke();

        var row = fixture.Owned("Rules, entry 1", TimeSpan.FromSeconds(10));
        Assert.True(row is not null, $"The sub-form did not open. Windows: {string.Join(" | ", fixture.Titles())}");

        try
        {
            // §7.17 again: without an explicit name this would announce its whole content.
            Assert.Equal("Rules, entry 1", row!.Name);

            var focusable = UiaTree.Descendants(row)
                .Where(e => UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)
                .Select(e => (Type: UiaTree.Safe(() => e.ControlType), Name: UiaTree.Safe(() => e.Name) ?? ""))
                .ToList();

            foreach (var (type, name) in focusable) output.WriteLine($"{type}:{name}");

            Assert.Contains(focusable, e => e.Name == "Field");
            Assert.Contains(focusable, e => e.Name == "Value");

            // §7.1, over the window's own controls. Framework chrome is named explicitly rather
            // than by loosening the rule that catches ours (§9).
            Assert.DoesNotContain(focusable, e =>
                e.Name.Length == 0 &&
                e.Type is ControlType.Edit or ControlType.ComboBox or ControlType.CheckBox
                       or ControlType.Button or ControlType.List or ControlType.DataItem);
        }
        finally
        {
            row!.Close();
            Input.Settle(200);
        }
    }

    // ------------------------------------------------------------------ virtualization

    [Fact]
    public void Every_field_of_a_forty_parameter_form_is_in_the_automation_tree()
    {
        // The guard §9 asks for. Phase 0 measured a 209-field form leaving 14 fields visible to UIA
        // with virtualization on; this fails at 40 the moment anybody turns it back on for
        // performance. Nothing about it is subtle once it breaks — the count is simply wrong.
        using var form = Open("Forty parameters", WideForm);
        form.ShowOptional();

        var boxes = UiaTree.Descendants(form.Fields)
            .Where(e => UiaTree.Safe(() => e.ControlType) == ControlType.Edit)
            .Select(e => UiaTree.Safe(() => e.Name) ?? "")
            .Where(n => n.StartsWith("Field ", StringComparison.Ordinal))
            .ToList();

        output.WriteLine($"{boxes.Count} of 40 fields reached the automation tree.");
        Assert.Equal(40, boxes.Count);
    }

    // ------------------------------------------------------------------ opening a form

    /// <summary>
    /// Opens a tool's form, through UI Automation rather than by typing.
    /// </summary>
    /// <remarks>
    /// Deliberately not keystrokes. Getting a form open is setup for almost every test in this
    /// file, and setup that needs the foreground is setup that fails whenever somebody is using the
    /// machine — which the suite is right to refuse to type into (§9), and which would then take
    /// every assertion here down with it. The keyboard route is a claim of its own and is asserted
    /// in <see cref="A_tool_with_parameters_opens_a_form_window_from_the_keyboard"/>.
    /// </remarks>
    private OpenForm Open(string toolLabel, string windowTitle)
    {
        if (fixture.Owned(windowTitle, TimeSpan.FromMilliseconds(200)) is { } already)
            return new OpenForm(already);

        Select(toolLabel);

        // The main window's own Run button, which is a different element from the one on a form —
        // and has a different automation id for that reason, since a form is a UIA descendant of
        // the window that opened it and an ambiguous lookup here pressed the wrong button.
        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))?.AsButton();
        Assert.True(run is not null, "The main window has no Run button.");

        WaitUntilEnabled(run!, $"The main Run button after selecting {toolLabel}");
        run!.Invoke();

        var window = fixture.Owned(windowTitle, TimeSpan.FromSeconds(10));
        Assert.True(window is not null,
            $"The form window {windowTitle} did not open. humcp has {(fixture.HasExited ? "exited" : "not exited")} " +
            $"and its windows are: {string.Join(" | ", fixture.Titles())}. " +
            $"Status line: {Main.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"))?.Name}");

        Input.Settle(300);
        return new OpenForm(window!);
    }

    /// <summary>
    /// Waits for a command-driven button to catch up. Enabled state follows the form through
    /// <c>CommandManager</c>'s re-query, so invoking one the instant its condition becomes true
    /// throws "element not enabled" — a defect in the test that reads like one in the application.
    /// </summary>
    private static void WaitUntilEnabled(AutomationElement element, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && UiaTree.Safe(() => element.Properties.IsEnabled.ValueOrDefault) != true)
            Thread.Sleep(100);

        Assert.True(UiaTree.Safe(() => element.Properties.IsEnabled.ValueOrDefault) == true,
            $"{what} never became enabled.");
    }

    private AutomationElement Select(string toolLabel)
    {
        var tree = Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Assert.NotNull(tree);

        var tool = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith(toolLabel, StringComparison.Ordinal));

        Assert.True(tool is not null, $"No tool called {toolLabel} in the tree.");

        tool!.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(150);

        return tool;
    }

    /// <summary>
    /// One open form window, closed when the test finishes with it. Left open, it would be in front
    /// of the next test's keystrokes.
    /// </summary>
    private sealed class OpenForm(Window window) : IDisposable
    {
        public Window Window { get; } = window;

        /// <summary>The generated form itself, which is a group and not the whole window.</summary>
        public AutomationElement Fields =>
            Window.FindFirstDescendant(cf => cf.ByAutomationId("Fields"))
            ?? throw new InvalidOperationException("The form is not in the UIA tree.");

        /// <summary>
        /// The input named <paramref name="label"/>. Focusable elements only: the visible label has
        /// the same name by design (§7.3 wants both), and it is the one that comes first.
        /// </summary>
        public AutomationElement? Control(string label) =>
            UiaTree.Descendants(Fields).FirstOrDefault(e =>
                UiaTree.Safe(() => e.Name) == label
                && UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true);

        /// <summary>Everything on the form that has a name, for a failure message worth reading.</summary>
        public string Names() => string.Join(" | ", UiaTree.Descendants(Fields)
            .Select(e => $"{UiaTree.Safe(() => e.ControlType)}:{UiaTree.Safe(() => e.Name)}")
            .Where(n => !n.EndsWith(':')));

        public void ShowOptional()
        {
            var toggle = Window.FindFirstDescendant(cf => cf.ByAutomationId("OptionalToggle"))?.AsCheckBox();
            if (toggle is null || toggle.IsChecked == true) return;

            // Toggled through the pattern rather than by typing: this is setup, and setup that
            // needs the foreground is setup that fails on a busy machine.
            toggle.Toggle();
            Settle();
        }

        /// <summary>
        /// Waits until the form has stopped growing rather than for a fixed number of milliseconds.
        /// Revealing thirteen hidden parameters realises thirteen templates, and on a loaded machine
        /// a test that reads the tree too early sees a control whose bindings have not run — which
        /// looks exactly like a control with no accessible name.
        /// </summary>
        private void Settle()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var previous = -1;

            while (DateTime.UtcNow < deadline)
            {
                Input.Settle(200);

                var count = UiaTree.Descendants(Fields).Count();
                if (count == previous && count > 0) return;

                previous = count;
            }
        }

        public string Result() =>
            Window.FindFirstDescendant(cf => cf.ByAutomationId("FormResultBox"))?.AsTextBox().Text ?? "";

        /// <summary>Runs the tool the way the Run button does, without needing the foreground.</summary>
        public void Run()
        {
            var run = Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))?.AsButton();
            run?.Invoke();
            Input.Settle(500);
        }

        public void FocusFirstField()
        {
            var first = UiaTree.Descendants(Fields)
                .FirstOrDefault(e => UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true);

            first?.Focus();
            Input.Settle(150);
        }

        public void Dispose()
        {
            try { Window.Close(); } catch { /* the window may already be gone */ }

            // Waited for rather than assumed. A window still closing when the next test starts is
            // one more thing in front of its keystrokes, and this suite is hard enough to keep
            // deterministic without that.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (DateTime.UtcNow < deadline && UiaTree.Safe(() => Window.IsAvailable) == true)
                Thread.Sleep(100);

            Input.Settle(200);
        }
    }
}

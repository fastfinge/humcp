using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// The window a server's own question opens, asserted against the live UIA tree of a running
/// application (ROADMAP §10, Phase 5; §9).
/// </summary>
/// <remarks>
/// <para>
/// Every one of these drives a real multi round-trip exchange: the fixture's tool answers with
/// <c>input_required</c>, humcp opens this window, and what the test presses decides what the
/// server is told. Nothing is stubbed, so a question that never reaches a window fails here rather
/// than in a comment.
/// </para>
/// <para>
/// As with the form suite, none of this proves the window is pleasant — only a screen reader does
/// that — but all of it fails the moment the message stops being focusable or the three answers
/// stop being three.
/// </para>
/// </remarks>
[Collection(AppCollection.Name)]
public class QuestionTests(AppFixture fixture, ITestOutputHelper output)
{
    private const string ConfirmQuestion = "Question — Ask before doing it";
    private const string BookForm = "Book a journey — humcp test server";
    private const string BookQuestion = "Question — Book a journey";
    private const string BookQuestion2 = "Question 2 — Book a journey";
    private const string WebQuestion = "A web page — Sign in on the web";
    private const string KindsQuestion = "Question — One field of each kind";

    private Window Main => fixture.Window;

    // ------------------------------------------------------------------ a question with no fields

    [Fact]
    public void A_tool_that_asks_before_acting_opens_a_window_that_names_itself()
    {
        using var question = AskFromTree("Ask before doing it", ConfirmQuestion);

        Assert.Equal(ConfirmQuestion, question.Window.Title);

        // §7.17: a WPF Window announces its whole content as its name unless told otherwise, which
        // here would be the server's message plus every button on the window.
        Assert.Equal(ConfirmQuestion, question.Window.Name);
    }

    [Fact]
    public void The_message_is_a_control_and_not_only_a_heading()
    {
        using var question = AskFromTree("Ask before doing it", ConfirmQuestion);

        var message = question.Element("QuestionMessage");

        // §7.28, learned in Phase 4 about a picture: a sentence in a TextBlock is on screen and in
        // nobody's ear. The server's message is the whole reason this window exists, so it is a
        // read-only caret-navigable box with a name (§7.9) rather than a label.
        Assert.Equal(ControlType.Edit, message.ControlType);
        Assert.True(message.Properties.IsKeyboardFocusable.ValueOrDefault);
        Assert.Equal("What the server is asking", message.Name);
        Assert.Contains("pretend to delete", message.AsTextBox().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_starts_on_the_message()
    {
        using var question = AskFromTree("Ask before doing it", ConfirmQuestion);

        // A form whose question was never read is a form answered blind, so the caret starts on
        // what the server said rather than on the first thing to fill in.
        var focused = fixture.Automation.FocusedElement();

        Assert.Equal("What the server is asking", UiaTree.Safe(() => focused?.Name));
    }

    [Fact]
    public void The_three_answers_are_three_named_buttons_with_help_on_them()
    {
        using var question = AskFromTree("Ask before doing it", ConfirmQuestion);

        foreach (var (id, name) in new[]
                 {
                     ("QuestionAccept", "Accept"),
                     ("QuestionDecline", "Decline"),
                     ("QuestionCancel", "Cancel"),
                 })
        {
            var button = question.Element(id);

            Assert.Equal(ControlType.Button, button.ControlType);
            Assert.Equal(name, button.Name);
            Assert.False(string.IsNullOrWhiteSpace(button.HelpText), $"{name} has no help text.");
        }

        // §7.27: a button bound to a RoutedUICommand announces that command's Text where a screen
        // reader says the shortcut, so the real keystroke is set here and spelled out (§7.15).
        Assert.Equal("Control plus Enter",
            question.Element("QuestionAccept").Properties.AcceleratorKey.ValueOrDefault);
    }

    [Fact]
    public void Nothing_but_real_controls_is_in_the_tab_order()
    {
        using var question = AskFromTree("Ask before doing it", ConfirmQuestion);

        Input.EnsureForeground(question.Window, fixture.Automation);
        question.Element("QuestionMessage").Focus();
        Input.Settle(150);

        var stops = Input.WalkTabOrder(question.Window, fixture.Automation, out var cycled);
        output.WriteLine(string.Join(Environment.NewLine, stops));

        Assert.True(cycled, "The tab order never came back to where it started: " + string.Join(" | ", stops));

        // §7.14: WPF tab-stops menu bars and scroll viewers by default, and a menu bar is reached
        // with Alt rather than with Tab.
        Assert.DoesNotContain(stops, s => s.StartsWith("MenuBar", StringComparison.Ordinal));
        Assert.DoesNotContain(stops, s => s.StartsWith("Pane:", StringComparison.Ordinal));
        Assert.Contains(stops, s => s.Contains("Accept", StringComparison.Ordinal));
        Assert.Contains(stops, s => s.Contains("Decline", StringComparison.Ordinal));
        Assert.Contains(stops, s => s.Contains("Cancel", StringComparison.Ordinal));
    }

    [Fact]
    public void Declining_tells_the_server_so()
    {
        using (var question = AskFromTree("Ask before doing it", ConfirmQuestion))
        {
            question.Element("QuestionDecline").AsButton().Invoke();
        }

        Assert.Contains("\"decline\"", WaitForResult(t => t.Contains("The answer was")), StringComparison.Ordinal);
    }

    [Fact]
    public void Accepting_tells_the_server_so()
    {
        using (var question = AskFromTree("Ask before doing it", ConfirmQuestion))
        {
            question.Element("QuestionAccept").AsButton().Invoke();
        }

        Assert.Contains("\"accept\"", WaitForResult(t => t.Contains("The answer was")), StringComparison.Ordinal);
    }

    [Fact]
    public void Escape_cancels_rather_than_closing_and_leaving_the_call_hanging()
    {
        using (var question = AskFromTree("Ask before doing it", ConfirmQuestion))
        {
            Input.EnsureForeground(question.Window, fixture.Automation);
            Input.Press(VirtualKeyShort.ESCAPE);
            Input.Settle(400);
        }

        // Closing the window is cancelling. A call left waiting on a window that no longer exists
        // is a call that never comes back, which is the version of this that has no symptom.
        Assert.Contains("\"cancel\"", WaitForResult(t => t.Contains("The answer was")), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ a question with fields

    [Fact]
    public void A_question_with_fields_shows_all_of_them_including_the_optional_one()
    {
        using var form = OpenBookForm();
        using var question = AskFromForm(form, BookQuestion);

        // A tool form hides its optional parameters (§4, pitfall 4). This one must not: the client
        // SDK writes every schema-stated default back into the answer before it goes on the wire,
        // so a field nobody saw is a field the server is told the user agreed to (phase5-notes 3.3).
        Assert.NotNull(question.Control("Departure date"));
        Assert.NotNull(question.Control("Passengers"));
        Assert.NotNull(question.Control("Flexible dates"));

        Assert.Null(question.Window.FindFirstDescendant(cf => cf.ByAutomationId("OptionalToggle")));
    }

    [Fact]
    public void The_generated_answer_is_a_group_of_groups_rather_than_a_list_of_data_items()
    {
        using var form = OpenBookForm();
        using var question = AskFromForm(form, BookQuestion);

        var fields = question.Element("QuestionFields");

        // §7.12 again, one window further along: the same ItemsControl defaults, the same fix, and
        // the same assertion that catches it coming back.
        Assert.Equal(ControlType.Group, fields.ControlType);
        Assert.DoesNotContain(UiaTree.Descendants(fields),
            e => UiaTree.Safe(() => e.ControlType) == ControlType.DataItem);
        Assert.False(fields.Properties.IsKeyboardFocusable.ValueOrDefault);
    }

    [Fact]
    public void Every_focusable_control_on_the_question_has_a_name()
    {
        using var form = OpenBookForm();
        using var question = AskFromForm(form, BookQuestion);

        // The window's own title bar is the platform's and is excluded; everything below it is
        // humcp's, including the pieces built in code rather than in XAML.
        var unnamed = UiaTree.Descendants(question.Window)
            .Where(e => UiaTree.Safe(() => e.ControlType) != ControlType.TitleBar)
            .Where(e => UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)
            .Where(e => string.IsNullOrWhiteSpace(UiaTree.Safe(() => e.Name)))
            .Select(e => UiaTree.Safe(() => e.ControlType).ToString() ?? "?")
            .ToList();

        Assert.True(unnamed.Count == 0, "Unnamed and focusable: " + string.Join(", ", unnamed));
    }

    /// <summary>Phase 5's exit criterion, driven through the windows a user would drive.</summary>
    [Fact]
    public void A_multi_step_tool_completes_through_two_question_windows()
    {
        using var form = OpenBookForm();

        using (var journey = AskFromForm(form, BookQuestion))
        {
            // Typed rather than set through a pattern, because this is the assertion that is about
            // the keyboard: the two required fields are adjacent in schema order, so Tab is the
            // whole of the journey between them (§7.7).
            Input.EnsureForeground(journey.Window, fixture.Automation);
            journey.Control("Departure date")!.Focus();
            Input.Settle(150);
            Input.Type("01/09/2026");
            Input.Press(VirtualKeyShort.TAB);
            Input.Type("2");

            journey.Element("QuestionAccept").AsButton().Invoke();
            Input.Settle(400);
        }

        // Titled as the second one, so that a window with the same message-shaped chrome is not
        // announced with the same name as the window it replaced (phase5-notes 4.2).
        using (var seat = new OpenWindow(
                   fixture.Owned(BookQuestion2, TimeSpan.FromSeconds(15))
                   ?? throw new InvalidOperationException(
                       "The second question never opened. Open windows: " + string.Join(" | ", fixture.Titles()))))
        {
            // The second question exists only because the first was answered, and it asks something
            // the first did not: this is the round trip rather than a repeat of it.
            var position = seat.Control("Seat position");
            Assert.NotNull(position);

            // Chosen rather than left alone. A required enumeration with nothing picked is exactly
            // the case the warn-once rule is about, and this test is not about that rule.
            var seats = position!.AsComboBox();
            seats.Select("On the aisle");
            seats.Collapse();
            Input.Settle(200);

            seat.Element("QuestionAccept").AsButton().Invoke();
            Input.Settle(400);
        }

        var result = WaitForFormResult(form, t => t.Contains("Booked."));
        output.WriteLine(UiaTree.Flatten(result, 300));

        Assert.Contains("Destination: Perth", result, StringComparison.Ordinal);
        Assert.Contains("Two round trips", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_required_answer_warns_once_rather_than_refusing()
    {
        using var form = OpenBookForm();
        using var question = AskFromForm(form, BookQuestion);

        question.Element("QuestionAccept").AsButton().Invoke();
        Input.Settle(500);

        // §5: client-side validation is advice and never a veto, here for the same reason it is on a
        // tool form — the server is the authority, and an over-strict client makes a working server
        // unusable. So the window is still open, the problems are listed, and a second press sends.
        Assert.True(UiaTree.Safe(() => question.Window.IsAvailable) == true,
            "The window closed rather than warning.");

        var problems = question.Element("QuestionProblems");
        var listed = problems.FindAllChildren().Select(e => UiaTree.Safe(() => e.Name)).ToList();

        Assert.NotEmpty(listed);
        output.WriteLine(string.Join(Environment.NewLine, listed));

        // And it says so where a screen reader will read it, not only in red (§7.20).
        var status = question.Element("QuestionStatus");
        Assert.Contains("Press Control plus Enter again",
            UiaTree.Safe(() => status.Name) ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_date_says_that_it_is_required()
    {
        using var form = OpenBookForm();
        using var question = AskFromForm(form, BookQuestion);

        // The innermost one, deliberately. A DatePicker is a composite: two elements carry the
        // name, and the one the keyboard lands on is the text box inside it — so a test that takes
        // the first match asserts against the picker and proves nothing about what is announced.
        var departure = question.Innermost("Departure date");
        var passengers = question.Innermost("Passengers");

        // Both are required by the same schema. The date is a composite whose focus lands on a text
        // box inside it, and the box does not inherit what was set on the picker — so it announced
        // "Departure date, edit" where the box beside it announced "required" (phase5-notes 4.3).
        Assert.True(passengers.Properties.IsRequiredForForm.ValueOrDefault, "Passengers lost its required state.");
        Assert.True(departure.Properties.IsRequiredForForm.ValueOrDefault,
            "The date does not report that it is required. It reports: " +
            string.Join(", ", UiaTree.Descendants(question.Window)
                .Where(e => UiaTree.Safe(() => e.Name) == "Departure date")
                .Select(e => $"{e.ControlType} required={UiaTree.Safe(() => e.Properties.IsRequiredForForm.ValueOrDefault)}")));

        // The access key is a different story and the fix is a different rule. It works — Alt plus
        // the letter lands in this box — but which letter it is cannot be got onto the box's own
        // peer, so §7.15 applies: a keystroke nothing announces is one the renderer spells out.
        Assert.Contains("Alt plus", departure.HelpText, StringComparison.Ordinal);
        Assert.Contains("moves here", departure.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_on_a_question_can_be_added_to()
    {
        using var question = AskFromTree("One field of each kind", KindsQuestion);

        var choice = question.Control("Pick several, unlabelled")!.AsComboBox();
        choice.Select("green");

        // Collapsed again, deliberately. FlaUI selects by opening the drop-down, and a WPF popup
        // left open holds the window: Close() is swallowed, the question is never answered, and the
        // call stays in flight — which the *next* test sees as a Run button that never enables.
        choice.Collapse();
        Input.Settle(200);

        var add = question.Element("__add__Pick several, unlabelled");

        // The templates raise commands and the window answers them; a window that binds none has a
        // form whose buttons are disabled, and a disabled button is not in the tab order at all —
        // so the help text went on saying "press Add" beside a button nothing could reach
        // (phase5-notes 4.9).
        WaitUntilEnabled(add, "Add to Pick several, unlabelled");
        add.AsButton().Invoke();
        Input.Settle(300);

        var entries = question.Element("__list__Pick several, unlabelled");

        Assert.Single(entries.FindAllChildren());

        // And the caret is back in the box rather than on the window. Adding empties the draft,
        // which disables the button being pressed, and WPF drops focus from a disabled control to
        // the window (§7.25, phase5-notes 4.10).
        var focused = fixture.Automation.FocusedElement();
        Assert.Equal("Pick several, unlabelled", UiaTree.Safe(() => focused?.Name));
        Assert.Contains("Added to Pick several, unlabelled",
            UiaTree.Safe(() => question.Element("QuestionStatus").Name) ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_puts_its_Add_button_after_the_box_it_adds_from()
    {
        using var question = AskFromTree("One field of each kind", KindsQuestion);

        // With something chosen, so that Add is enabled: a disabled button is not focusable and not
        // in the tab order, which is a different defect with the same symptom.
        var choice = question.Control("Pick several, unlabelled")!.AsComboBox();
        choice.Select("green");
        choice.Collapse();
        Input.Settle(300);

        // Focusable elements only, which is the whole of the point. The visible Label carries the
        // same name as the box it labels, by design (§7.3) — so the first assertion written here
        // compared the *Label*'s position with the button's, passed, and proved nothing about the
        // tab order it was named for. A test that can pass without the fix is not a test.
        var order = UiaTree.Descendants(question.Window)
            .Where(e => UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)
            .Select(e => UiaTree.Safe(() => e.Name) ?? "")
            .ToList();

        var box = order.IndexOf("Pick several, unlabelled");
        var add = order.IndexOf("Add to Pick several, unlabelled");

        Assert.True(box >= 0 && add >= 0,
            "The list's box and Add button are not both focusable. Focusable here: " +
            string.Join(" | ", order));

        // Tab order follows the tree and not the screen (phase3-notes 4.3, and §7.7). The button is
        // drawn to the right of the box and used to be declared before it, so the box's own help
        // text — "choose a value and press Add" — sent the user forward past a button that was
        // behind them. The same is true of every list template, which is why one assertion covers
        // them: they were all one DockPanel apiece.
        Assert.True(box < add,
            "The Add button comes before the box it adds from, so Tab reaches it before there is " +
            "anything to add.");
    }

    // ------------------------------------------------------------------ the mode humcp declines

    [Fact]
    public void An_out_of_band_web_request_is_explained_and_cannot_be_accepted()
    {
        using var question = AskFromTree("Sign in on the web", WebQuestion);

        // humcp declares form mode only (§11.3 defers the OAuth-shaped problem to v3), and a server
        // can ask anyway. The address is shown so the user knows what was wanted, in a control they
        // can read and copy — and there is no button that would follow it.
        var address = question.Element("QuestionUrl");

        Assert.Equal(ControlType.Edit, address.ControlType);
        Assert.Contains("example.com/authorise", address.AsTextBox().Text, StringComparison.Ordinal);

        Assert.Null(question.Window.FindFirstDescendant(cf => cf.ByAutomationId("QuestionAccept")));
        Assert.NotNull(question.Window.FindFirstDescendant(cf => cf.ByAutomationId("QuestionDecline")));
    }

    // ------------------------------------------------------------------ what humcp will not do

    [Fact]
    public void A_request_for_a_language_model_never_opens_a_window_and_says_why()
    {
        Run("Ask for a language model");

        var result = WaitForResult(t => t.Contains("language model"));

        Assert.Contains("humcp for a language model", result, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Titles(), t => t.StartsWith("Question —", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private OpenWindow AskFromTree(string toolLabel, string questionTitle)
    {
        Run(toolLabel);

        return new OpenWindow(
            fixture.Owned(questionTitle, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException(
                $"No window called \"{questionTitle}\" opened. Open windows: " +
                string.Join(" | ", fixture.Titles())));
    }

    /// <summary>
    /// The Book a journey form, with its one required parameter filled in and not yet run.
    /// </summary>
    private OpenWindow OpenBookForm()
    {
        Run("Book a journey");

        var form = new OpenWindow(
            fixture.Owned(BookForm, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException(
                "The form never opened. Open windows: " + string.Join(" | ", fixture.Titles())));

        var destination = form.Control("Destination")
                          ?? throw new InvalidOperationException("The form has no Destination field.");

        destination.Patterns.Value.Pattern.SetValue("Perth");
        Input.Settle(150);

        return form;
    }

    private OpenWindow AskFromForm(OpenWindow form, string questionTitle)
    {
        form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))?.AsButton().Invoke();

        return new OpenWindow(
            fixture.Owned(questionTitle, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException(
                $"No window called \"{questionTitle}\" opened. Open windows: " +
                string.Join(" | ", fixture.Titles())));
    }

    /// <summary>
    /// Selects a tool and runs it, through UI Automation patterns rather than keystrokes.
    /// </summary>
    /// <remarks>
    /// §9: setup is driven by patterns, which need no foreground; keystrokes are for the assertions
    /// that are about the keyboard. Every one of these tests needs a question window open and only
    /// some of them are about how it got there, and a shared helper that types made the rest fail
    /// together whenever something else on the desktop held focus.
    /// </remarks>
    private void Run(string toolNamePrefix)
    {
        var tree = Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))
                   ?? throw new InvalidOperationException("No tree in the window.");

        var item = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                       .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "")
                           .StartsWith(toolNamePrefix, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"No tree item starts with \"{toolNamePrefix}\".");

        item.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(150);

        var run = Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))?.AsButton()
                  ?? throw new InvalidOperationException("The main window has no Run button.");

        WaitUntilEnabled(run, $"The main Run button after selecting {toolNamePrefix}");
        run.Invoke();
        Input.Settle(300);
    }

    private void WaitUntilEnabled(AutomationElement element, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline && UiaTree.Safe(() => element.Properties.IsEnabled.ValueOrDefault) != true)
            Thread.Sleep(100);

        Assert.True(UiaTree.Safe(() => element.Properties.IsEnabled.ValueOrDefault) == true,
            $"{what} never became enabled. The status line says: " +
            (Main.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"))?.Name ?? "(nothing)") +
            ". Open windows: " + string.Join(" | ", fixture.Titles()));
    }

    private string WaitForResult(Func<string, bool> predicate)
        => WaitFor(() => Main.FindFirstDescendant(cf => cf.ByAutomationId("MainResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text ?? "", predicate);

    private static string WaitForFormResult(OpenWindow form, Func<string, bool> predicate)
        => WaitFor(() => form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text ?? "", predicate);

    private static string WaitFor(Func<string> read, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var last = "";

        while (DateTime.UtcNow < deadline)
        {
            last = UiaTree.Safe(read) ?? "";
            if (predicate(last)) return last;
            Thread.Sleep(150);
        }

        throw new InvalidOperationException("The result never said what was expected. It says: " + last);
    }

    /// <summary>One window humcp opened, closed when the test has finished with it.</summary>
    private sealed class OpenWindow(Window window) : IDisposable
    {
        public Window Window { get; } = window;

        public AutomationElement Element(string automationId)
        {
            // "__add__X" and "__list__X" look the controls up by their accessible name instead:
            // the templates identify a field's own buttons by name, because the name is generated
            // from the field's label and an id could not be.
            if (automationId.StartsWith("__add__", StringComparison.Ordinal))
                return Named($"Add to {automationId["__add__".Length..]}");

            if (automationId.StartsWith("__list__", StringComparison.Ordinal))
                return Named($"{automationId["__list__".Length..]}, entries added");

            return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
                   ?? throw new InvalidOperationException(
                       $"No element with id {automationId} in \"{Window.Title}\".");
        }

        private AutomationElement Named(string name)
            => UiaTree.Descendants(Window).FirstOrDefault(e => UiaTree.Safe(() => e.Name) == name)
               ?? throw new InvalidOperationException(
                   $"Nothing called \"{name}\" in \"{Window.Title}\".");

        /// <summary>
        /// The input named <paramref name="label"/>. Focusable elements only: the visible label
        /// carries the same name by design (§7.3 wants both), and it comes first in the tree.
        /// </summary>
        public AutomationElement? Control(string label) =>
            UiaTree.Descendants(Window).FirstOrDefault(e =>
                UiaTree.Safe(() => e.Name) == label
                && UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true);

        /// <summary>
        /// The deepest focusable control carrying <paramref name="label"/>, which for a composite
        /// is the part that actually takes the focus (§9, phase5-notes 4.3).
        /// </summary>
        public AutomationElement Innermost(string label) =>
            UiaTree.Descendants(Window).LastOrDefault(e =>
                UiaTree.Safe(() => e.Name) == label
                && UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)
            ?? throw new InvalidOperationException($"Nothing focusable called \"{label}\".");

        public void Dispose()
        {
            try { Window.Close(); } catch { /* the window may already be gone */ }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (DateTime.UtcNow < deadline && UiaTree.Safe(() => Window.IsAvailable) == true)
                Thread.Sleep(100);

            Input.Settle(200);
        }
    }
}

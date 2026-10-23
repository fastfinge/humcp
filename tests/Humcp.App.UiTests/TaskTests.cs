using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// Background tasks (ROADMAP §11, Phase 14): a call the server makes a task, waited for in a form,
/// handed over from the tree, carried on after its window closes, stopped, and listed.
/// </summary>
/// <remarks>Driven through UI Automation patterns: nothing here is a claim about a keystroke.</remarks>
[Collection(AppCollection.Name)]
public class TaskTests(AppFixture fixture, ITestOutputHelper output)
{
    private const string WaitForm = "Work in the background — humcp test server";
    private const string TaskList = "Background tasks";

    private Window Main => fixture.Window;

    [Fact]
    public void A_task_in_a_form_shows_its_result_there_and_is_listed()
    {
        using (var form = Open("Work in the background", WaitForm))
        {
            Run(form, seconds: 1);

            var text = WaitFor(() => form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormResults"))
                ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text is { } t
                && t.Contains("Worked for 1 seconds") ? t : null, "the task's result in the form");

            Assert.Contains("Worked for 1 seconds", text);
        }

        using var list = OpenTaskList();
        var entry = WaitFor(() => Entries(list.Window)
            .FirstOrDefault(e => e.StartsWith("Work in the background on humcp test server, finished", StringComparison.Ordinal)),
            "the finished task in the list");

        output.WriteLine(entry);
    }

    [Fact]
    public void A_task_run_from_the_tree_is_handed_to_the_list_and_said_when_done()
    {
        Invoke("Write a slow report");

        // Still running after a moment, so the tree is given back and the user is told how it goes on.
        WaitFor(() => Status(Main, "LiveStatus") is { } s && s.Contains("is running in the background. Control plus T lists it") ? s : null,
            "the hand-over");

        Assert.True(Main.FindFirstDescendant(cf => cf.ByAutomationId("RunButton"))!.AsButton().IsEnabled,
            "The tree's Run button stayed disabled for a task that was handed over.");

        WaitFor(() => Status(Main, "LiveStatus") is { } s && s.Contains("Write a slow report has finished") ? s : null,
            "the task's ending, said unprompted");

        using var list = OpenTaskList();
        Select(list.Window, "Write a slow report on humcp test server, finished");

        var text = WaitFor(() => list.Window.FindFirstDescendant(cf => cf.ByAutomationId("TasksResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text is { } t
            && t.Contains("three sections") ? t : null, "the report in the task list");

        Assert.Contains("rainfall", text);
    }

    [Fact]
    public void Closing_a_form_leaves_its_task_running_and_its_ending_is_said()
    {
        using (var form = Open("Work in the background", WaitForm))
        {
            Run(form, seconds: 3);

            WaitFor(() => Status(form.Window, "FormStatus") is { } s && s.Contains("Closing this window will not stop it") ? s : null,
                "the form saying it can be closed");
        }

        // The form is gone and the work is not: the main window has the focus, and hears the end.
        WaitFor(() => Status(Main, "LiveStatus") is { } s && s.Contains("Work in the background has finished") ? s : null,
            "the ending of a task whose window closed");
    }

    [Fact]
    public void Stop_asks_the_server_and_the_task_ends_stopped()
    {
        using (var form = Open("Work in the background", WaitForm))
        {
            Run(form, seconds: 60);

            var stop = WaitFor(() => form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormStopButton"))?.AsButton()
                is { IsEnabled: true } b ? b : null, "the Stop button");

            // Long enough for the call to have become a task, so this is tasks/cancel and not the end of a request.
            Thread.Sleep(800);
            stop.Invoke();

            WaitFor(() => Status(form.Window, "FormStatus") is { } s && s.StartsWith("Stopped Work in the background", StringComparison.Ordinal)
                ? s : null, "the form saying it stopped");
        }

        using var list = OpenTaskList();
        WaitFor(() => Entries(list.Window)
            .FirstOrDefault(e => e.StartsWith("Work in the background on humcp test server, stopped", StringComparison.Ordinal)),
            "the stopped task in the list");
    }

    [Fact]
    public void The_task_list_is_named_and_says_what_it_holds()
    {
        using var list = OpenTaskList();

        var box = list.Window.FindFirstDescendant(cf => cf.ByAutomationId("TasksList"));
        Assert.NotNull(box);
        Assert.Equal("Background tasks", UiaTree.Safe(() => box.Name));
        Assert.Contains("Delete stops", UiaTree.Safe(() => box.HelpText) ?? "");

        var status = WaitFor(() => Status(list.Window, "TasksStatus") is { Length: > 0 } s ? s : null, "the summary");
        output.WriteLine(status);
        Assert.True(status.StartsWith("No background tasks yet", StringComparison.Ordinal) || status.Contains(" task"),
            $"The list opened saying: {status}");

        // Every focusable thing in it has a name (§7.3), including the items, which say their description (§7.23).
        var unnamed = UiaTree.Descendants(list.Window)
            // The status bar reports itself focusable and is in no tab order; AccessibilityTests skips it too.
            .Where(e => UiaTree.Safe(() => e.ControlType) is not (ControlType.TitleBar or ControlType.StatusBar))
            .Where(e => UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)
            .Where(e => string.IsNullOrWhiteSpace(UiaTree.Safe(() => e.Name)))
            .Select(e => UiaTree.Safe(() => e.ControlType).ToString() ?? "?")
            .ToList();

        Assert.True(unnamed.Count == 0, "Unnamed and focusable: " + string.Join(", ", unnamed));
        Assert.DoesNotContain(Entries(list.Window), e => e.Contains("TaskEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void A_task_that_asks_opens_a_question_and_finishes_with_the_answer()
    {
        Invoke("Ask during a task");

        var question = fixture.Owned("Question — Ask during a task", TimeSpan.FromSeconds(15));
        Assert.True(question is not null, $"No question opened. Windows: {string.Join(" | ", fixture.Titles())}");

        var title = WaitFor(() => question!.FindFirstDescendant(cf => cf.ByAutomationId("QuestionFields"))
            ?.FindAllDescendants()
            .FirstOrDefault(e => UiaTree.Safe(() => e.Name) == "Report title"
                                 && UiaTree.Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true),
            "the Report title field");

        title.Patterns.Value.Pattern.SetValue("Rain");
        Input.Settle(200);
        question!.FindFirstDescendant(cf => cf.ByAutomationId("QuestionAccept"))!.AsButton().Invoke();

        // Answered within the moment the tree waits, or handed over and said: either way it is heard.
        WaitFor(() => Status(Main, "LiveStatus") is { } s
                      && (s.Contains("Ask during a task has finished") || s.StartsWith("Tool call complete", StringComparison.Ordinal))
            ? s : null, "the task finishing with the answer");

        using var list = OpenTaskList();
        Select(list.Window, "Ask during a task on humcp test server, finished");

        WaitFor(() => list.Window.FindFirstDescendant(cf => cf.ByAutomationId("TasksResults"))
            ?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox().Text is { } t
            && t.Contains("The report is called Rain.") ? t : null, "the answer in the result");
    }

    // ------------------------------------------------------------------ helpers

    private static string? Status(Window window, string id)
        => window.FindFirstDescendant(cf => cf.ByAutomationId(id))?.Name;

    private void Run(OpenWindow form, int seconds)
    {
        var field = form.Control("Seconds");
        Assert.True(field is not null, "No Seconds field. The form holds: " + form.Names());

        field!.Patterns.Value.Pattern.SetValue(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Input.Settle(200);

        form.Window.FindFirstDescendant(cf => cf.ByAutomationId("FormRunButton"))!.AsButton().Invoke();
    }

    /// <summary>Opens the list from the main window's Window menu, which every window carries.</summary>
    private OpenWindow OpenTaskList()
    {
        if (fixture.Owned(TaskList, TimeSpan.FromMilliseconds(200)) is { } already) return new OpenWindow(already);

        var menu = Main.FindFirstDescendant(cf => cf.ByName("Main menu"))!;
        var windowMenu = UiaTree.Descendants(menu).First(e => UiaTree.Safe(() => e.Name) == "Window");

        windowMenu.Patterns.ExpandCollapse.Pattern.Expand();
        Input.Settle(300);

        var item = WaitFor(() => UiaTree.Descendants(windowMenu)
            .FirstOrDefault(e => UiaTree.Safe(() => e.Name) == "Background tasks"), "the Background tasks menu item");
        item.Patterns.Invoke.Pattern.Invoke();

        var window = fixture.Owned(TaskList, TimeSpan.FromSeconds(10));
        Assert.True(window is not null, $"The task list did not open. Windows: {string.Join(" | ", fixture.Titles())}");

        Input.Settle(300);
        return new OpenWindow(window!);
    }

    private static List<string> Entries(Window list)
        => list.FindFirstDescendant(cf => cf.ByAutomationId("TasksList"))
               ?.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
               .Select(i => UiaTree.Safe(() => i.Name) ?? "")
               .ToList()
           ?? [];

    private static void Select(Window list, string prefix)
    {
        var item = WaitFor(() => list.FindFirstDescendant(cf => cf.ByAutomationId("TasksList"))
            ?.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(i => (UiaTree.Safe(() => i.Name) ?? "").StartsWith(prefix, StringComparison.Ordinal)),
            $"a task starting \"{prefix}\"");

        item.Patterns.SelectionItem.Pattern.Select();
        Input.Settle(300);
    }

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

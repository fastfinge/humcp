using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Humcp.Core;

namespace Humcp.App;

/// <summary>
/// The background task list (ROADMAP §11, Phase 14): a list of every task, and the selected one's
/// result in the same result view a form uses — so a table is a grid and a picture has its Save
/// button here too, and nothing about a task's result is read differently from a call's.
/// </summary>
internal partial class TaskListWindow : Window, IStatusLine
{
    private readonly BackgroundTasks _tasks;

    /// <summary>What the pane last showed the selected task as, so a change to it rebuilds only when it is news.</summary>
    private BackgroundTaskState? _shownState;

    public TaskListWindow(Window owner, BackgroundTasks tasks, WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(windows);

        InitializeComponent();

        _tasks = tasks;
        Owner = owner;

        // The window's own name, or a WPF window announces its whole content as one (§7.17).
        AutomationProperties.SetName(this, Title);

        TasksResults.Owner = this;
        TasksResults.Windows = windows;
        TasksResults.Announce = Say;
        TasksResults.Describer = tasks.Describer;
        TasksResults.ReadResource = (uri, label) =>
        {
            if (Selected is { } task) tasks.ReadResource?.Invoke(task.Connection, uri, label, this);
        };

        TasksList.ItemsSource = tasks.Entries;
        TasksList.SelectionChanged += (_, _) => ShowSelected();

        // §7.29: a list selects what it focuses, or Delete and the result pane have nothing to act on.
        TasksList.GotKeyboardFocus += (_, _) =>
        {
            if (TasksList.SelectedItem is null && TasksList.Items.Count > 0) TasksList.SelectedIndex = 0;
        };

        // Before Track, whose own Closing handler activates the owner and so ends this window's
        // being active before a handler added after it could see that it was.
        var wasActive = false;
        Closing += (_, e) => wasActive = !e.Cancel && IsActive;

        windows.Track(this);
        windows.Attach(this, TasksWindowMenu, closable: true);

        PreviewKeyDown += OnKey;

        Loaded += (_, _) =>
        {
            if (TasksList.SelectedItem is null && TasksList.Items.Count > 0) TasksList.SelectedIndex = 0;
            ShowSelected();
            FocusList();
            Say(Summary());
        };

        // Escape goes back to the window Control plus T was pressed in. After the close, not during
        // it: Windows gives the foreground to the owner (the main window) once an owned window has
        // gone, whatever was activated while it was going — first tried that way, and heard landing
        // in the tree (phase14-notes). By then the foreground is humcp's, so humcp may move it.

        Closed += (_, _) =>
        {
            TasksResults.StopDescribing();

            if (wasActive && ReturnTo is { IsVisible: true } back)
                _ = Dispatcher.BeginInvoke(() => WindowManager.Activate(back), DispatcherPriority.ApplicationIdle);
        };
    }

    /// <summary>The window the list was opened from, which closing it returns to while it is still open.</summary>
    public Window? ReturnTo { get; set; }

    private BackgroundTask? Selected => (TasksList.SelectedItem as TaskEntry)?.Task;

    public void Say(string text) => LiveRegion.Say(TasksStatus, text);

    /// <summary>Selects a task, and puts the caret on it.</summary>
    public void Select(BackgroundTask task)
    {
        var entry = _tasks.Entries.FirstOrDefault(e => ReferenceEquals(e.Task, task));
        if (entry is null) return;

        TasksList.SelectedItem = entry;
        FocusList();
    }

    /// <summary>
    /// A task changed. Only the selected task's result is rebuilt, and only when it has something
    /// new to show: rebuilding destroys whatever control the caret was in (§7.25).
    /// </summary>
    public void Changed(BackgroundTask task)
    {
        UpdateStop();
        if (ReferenceEquals(task, Selected) && task.State != _shownState) ShowSelected();
    }

    /// <summary>"3 tasks, 1 running." — or what to expect, when there are none.</summary>
    private string Summary()
    {
        var all = _tasks.Entries.Count;
        if (all == 0)
            return "No background tasks yet. When a server runs a tool in the background, it is listed here.";

        var running = _tasks.Running.Count;
        var tasks = all == 1 ? "1 task" : $"{all} tasks";

        return running == 0 ? $"{tasks}, none running." : $"{tasks}, {running} running.";
    }

    private void ShowSelected()
    {
        UpdateStop();

        var task = Selected;
        _shownState = task?.State;

        if (task is null)
        {
            TasksResultHeading.Text = "Result";
            TasksResults.ShowMessage("Result", "No task is selected.", "Select a task in the list to see its result here.");
            return;
        }

        var wasReading = TasksResults.IsKeyboardFocusWithin;

        if (task.Outcome is { } outcome)
        {
            TasksResultHeading.Text = outcome.IsError
                ? $"Result — {task.Tool.DisplayName} — error"
                : $"Result — {task.Tool.DisplayName}";
            TasksResults.Show(task.Tool, outcome);
        }
        else
        {
            TasksResultHeading.Text = $"Result — {task.Tool.DisplayName}";
            TasksResults.ShowMessage("Result", task.State switch
            {
                BackgroundTaskState.NeedsAnswer => "It is waiting for your answer to a question, in a window of its own.",
                BackgroundTaskState.Stopped => "It was stopped before it finished, so there is no result.",
                _ => "It is still running. Its result appears here when it finishes.",
            });
        }

        if (wasReading && !TasksResults.IsKeyboardFocusWithin) TasksResults.FocusFirst();
    }

    /// <summary>
    /// Stop is only there for a task that can still be stopped, and a disabled button is out of the
    /// tab order rather than merely inert — which is what is wanted here.
    /// </summary>
    private void UpdateStop()
    {
        var stoppable = Selected is { IsFinished: false, StopRequested: false };
        TasksStopButton.IsEnabled = stoppable;
        TasksStopMenuItem.IsEnabled = stoppable;
    }

    private void OnStop(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        if (Selected is not { } task) return;

        if (task.IsFinished)
        {
            Say($"{task.Tool.DisplayName} has already ended.");
            return;
        }

        if (TasksStopButton.IsKeyboardFocused) FocusList();

        _ = task.StopAsync();
        Say($"Asked {task.Connection.Descriptor.DisplayName} to stop {task.Tool.DisplayName}.");
        UpdateStop();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && TasksList.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Stop();
            return;
        }

        // Ctrl+M summarises the selected task's result, as it does a form's (Phase 11).
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            TasksResults.Summarise();
            return;
        }

        // F6 and Shift+F6 between the two panes, as in every other window.
        if (e.Key == Key.F6 && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = true;

            if (TasksList.IsKeyboardFocusWithin) TasksResults.FocusFirst();
            else FocusList();
        }
    }

    private void FocusList()
    {
        if (TasksList.SelectedItem is { } item
            && TasksList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
        {
            container.Focus();
            return;
        }

        TasksList.Focus();
    }
}

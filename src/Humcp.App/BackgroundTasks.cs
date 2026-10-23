using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Humcp.Core;

namespace Humcp.App;

/// <summary>
/// A window that has a status line of its own, so something said unprompted can be said where the
/// user is rather than in the main window behind them — a live region is read only in the window
/// that has the focus (§7's core rule).
/// </summary>
internal interface IStatusLine
{
    void Say(string text);
}

/// <summary>One line of the background task list: the task, and what it says about itself now.</summary>
internal sealed class TaskEntry(BackgroundTask task) : INotifyPropertyChanged
{
    public BackgroundTask Task { get; } = task;

    public string Description { get; private set; } = task.Describe();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        var now = Task.Describe();
        if (now == Description) return;

        Description = now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
    }

    /// <summary>What a list read through UI Automation says for it (§7.23).</summary>
    public override string ToString() => Description;
}

/// <summary>
/// Every task a server ran for humcp this session (ROADMAP §11, Phase 14), the window that lists
/// them, and what happens to a task nobody is waiting for any more.
/// </summary>
/// <remarks>
/// <para>
/// A task is waited for by the window that started it, and that window shows its result and says
/// so, as for any call. Two things end the waiting without ending the task: closing that window, and
/// the main window handing a long task over so the tree is free again. A task in either state is
/// <em>adopted</em> here — its questions open over the main window, and its ending is said wherever
/// the user is, because the window that would have said it is gone or busy.
/// </para>
/// <para>
/// Everything here runs on the UI thread. A task changes on a pool thread and is marshalled in.
/// </para>
/// </remarks>
internal sealed class BackgroundTasks
{
    private readonly Window _main;
    private readonly WindowManager _windows;
    private readonly Action<string> _sayWhereTheUserIs;
    private readonly Action<string> _speakIfAway;

    /// <summary>The window waiting for each task, while one is.</summary>
    private readonly Dictionary<BackgroundTask, Window> _waiting = [];

    private readonly HashSet<BackgroundTask> _ended = [];

    /// <summary>When each task was let go of, so its ending is not said on top of the sentence saying so.</summary>
    private readonly Dictionary<BackgroundTask, DateTime> _released = [];

    /// <summary>
    /// How long after a task is let go of before its ending may be said. Found by ear: a task that
    /// finished a tenth of a second after the main window handed it over cut off the sentence that
    /// said it had been, whatever the hand-over time was (phase14-notes).
    /// </summary>
    private static readonly TimeSpan HeardAfter = TimeSpan.FromSeconds(3);
    private TaskListWindow? _window;

    public BackgroundTasks(Window main, WindowManager windows, Action<string> sayWhereTheUserIs, Action<string> speakIfAway)
    {
        _main = main;
        _windows = windows;
        _sayWhereTheUserIs = sayWhereTheUserIs;
        _speakIfAway = speakIfAway;
    }

    /// <summary>Newest first: the one just started is the one most likely to be looked for.</summary>
    public ObservableCollection<TaskEntry> Entries { get; } = [];

    /// <summary>Reads a resource a task's result links to, through the task's server (Phase 13).</summary>
    public Action<ServerConnection, string, string, Window>? ReadResource { get; set; }

    /// <summary>Alt text for pictures in a task's result (Phase 10).</summary>
    public LocalModelFeatures? Describer { get; set; }

    /// <summary>Tasks still running, which closing humcp would lose.</summary>
    public IReadOnlyList<BackgroundTask> Running => [.. Entries.Select(e => e.Task).Where(t => !t.IsFinished)];

    /// <summary>A call became a task, and <paramref name="waiting"/> is waiting for its answer.</summary>
    public void Add(BackgroundTask task, Window waiting)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (Entries.Any(e => ReferenceEquals(e.Task, task))) return;

        _waiting[task] = waiting;
        Entries.Insert(0, new TaskEntry(task));

        task.Changed += (_, _) => _main.Dispatcher.BeginInvoke(() => OnChanged(task));

        // It may have changed before the handler was there to hear it.
        OnChanged(task);
    }

    /// <summary>
    /// Nobody is waiting for this task any more. Its questions come to the main window from now on,
    /// and its ending is said wherever the user is.
    /// </summary>
    public void Release(BackgroundTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        _waiting.Remove(task);
        _released[task] = DateTime.UtcNow;
        task.Prompter = (request, cancellationToken) => AskAsync(task, request, cancellationToken);
    }

    /// <summary>Opens the list, or brings it forward, with <paramref name="select"/> chosen in it.</summary>
    public void Show(BackgroundTask? select = null)
    {
        // The window it is opened from, to go back to: owned by the main window so that it outlives
        // any form, and so on its own it would return the caret there (phase14-notes).
        var from = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        if (_window is null)
        {
            _window = new TaskListWindow(_main, this, _windows);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        else
        {
            WindowManager.Activate(_window);
        }

        if (from is not null && !ReferenceEquals(from, _window)) _window.ReturnTo = from;
        if (select is not null) _window.Select(select);
    }

    private void OnChanged(BackgroundTask task)
    {
        foreach (var entry in Entries.Where(e => ReferenceEquals(e.Task, task))) entry.Refresh();
        _window?.Changed(task);

        if (!task.IsFinished || !_ended.Add(task)) return;

        // The window waiting for it says so itself when it is the one being read. Otherwise —
        // closed, handed over, or open behind another — it is said here, where the user is.
        if (_waiting.Remove(task, out var waiting) && waiting.IsActive) return;

        var ending = task.Ending();
        if (ending.Length == 0) return;

        var wait = _released.Remove(task, out var at) ? at + HeardAfter - DateTime.UtcNow : TimeSpan.Zero;

        if (wait <= TimeSpan.Zero)
        {
            Say(ending);
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = wait };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Say(ending);
        };
        timer.Start();
    }

    private void Say(string text)
    {
        _sayWhereTheUserIs(text);
        _speakIfAway(text);
    }

    /// <summary>A question from a task whose window has gone, asked over the main window.</summary>
    private async Task<ElicitationAnswer> AskAsync(BackgroundTask task, ElicitationRequest request, CancellationToken cancellationToken)
    {
        var name = task.Tool.DisplayName;
        var title = request.Kind == ElicitationKind.WebPage ? $"A web page — {name}" : $"Question — {name}";

        await _main.Dispatcher.InvokeAsync(() => _speakIfAway($"{name} is asking a question."));

        return await ElicitationWindow.AskAsync(_main, title, request, _windows, cancellationToken);
    }
}

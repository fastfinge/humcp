using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>One line of the error summary, and the field it is about.</summary>
/// <remarks>
/// A class with a <c>ToString</c> rather than a record, because a list box item's accessible name
/// is the plain text of its content — and a record's <c>ToString</c> reads out every property it
/// has, field path and keyword included (the same trap as §7.12's <c>DataItem</c> names).
/// </remarks>
internal sealed class ProblemEntry(string text, string fieldPath)
{
    public string FieldPath { get; } = fieldPath;

    public override string ToString() => text;
}

/// <summary>
/// A form generated from one tool's input schema, and the window that runs it (ROADMAP §10,
/// Phase 3).
/// </summary>
/// <remarks>
/// <para>
/// No part of this file knows anything about any particular tool, and that is the whole thesis
/// (§1): a server publishes machine-readable metadata, <see cref="FormBuilder"/> turns it into
/// fields, <see cref="FieldTemplateSelector"/> turns those into controls, and what comes out is
/// navigable by keyboard and readable by a screen reader without anyone having written a line of
/// code about that server.
/// </para>
/// <para>
/// The one judgement it makes is when to argue. Client-side validation is advice and never a veto
/// (§5) — so a form with problems on it warns once, says how many, moves the caret to the first of
/// them, and sends the call unchanged if the user asks a second time. The only refusal is text in a
/// Tier 3 editor that is not JSON, where there is no request to construct at all.
/// </para>
/// </remarks>
public partial class ToolWindow : Window, IStatusLine
{
    private readonly ToolInfo _tool;
    private readonly ServerConnection _connection;
    private readonly MruHistory _history;
    private readonly WindowManager _windows;
    private readonly FormState _state;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>The call in flight, cancelled by Stop or Escape without closing the window (Phase 7).</summary>
    private CancellationTokenSource? _call;

    /// <summary>
    /// The task the call in flight became, when the server made it one (Phase 14). Stopping it is a
    /// request to the server; closing the window only stops waiting for it.
    /// </summary>
    private BackgroundTask? _task;

    /// <summary>
    /// Set by the first Run of a tool that says it is destructive, which only warns; the second runs
    /// it. Cleared after every run, so each one is asked about (ROADMAP §3).
    /// </summary>
    private bool _destructiveConfirmed;
    private readonly Action<string> _speakIfAway;

    private string? _warnedAbout;
    private bool _callInFlight;
    private int _paneIndex;

    /// <summary>Set when Run is pressed from its own button, whose caret then belongs on Stop.</summary>
    private bool _toStop;

    /// <summary>How many questions this call has asked, so a second one is titled as a second one.</summary>
    private int _questions;

    /// <summary>When the result on screen was asked for, for the details window (F1).</summary>
    private DateTimeOffset _lastOutcomeAt;

    /// <param name="showOptional">
    /// Whether the optional parameters start out showing — the user's setting, off unless they
    /// changed it (§4, pitfall 4).
    /// </param>
    /// <param name="speakIfAway">
    /// Says something through the screen reader when no humcp window is in front and the user has
    /// asked for that (ROADMAP §13). A completion in a window nobody is looking at is otherwise
    /// silent, because a live region is read only in the window that has the focus.
    /// </param>
    public ToolWindow(
        Window owner, ToolInfo tool, ServerConnection connection, MruHistory history, WindowManager windows,
        bool showOptional = false, Action<string>? speakIfAway = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(windows);

        InitializeComponent();

        _tool = tool;
        _connection = connection;
        _history = history;
        _windows = windows;
        _speakIfAway = speakIfAway ?? (_ => { });
        _state = FormState.Create(tool.Form, tool.Kind switch
        {
            OperationKind.Prompt => AccessKeys.PromptCommands,
            OperationKind.Resource or OperationKind.ResourceTemplate => AccessKeys.ResourceCommands,
            _ => AccessKeys.WindowCommands,
        });
        _state.ShowOptional = showOptional;

        ApplyHistory();
        OfferResources();
        OfferSuggestions();

        Owner = owner;
        DataContext = _state;

        Title = $"{tool.DisplayName} — {connection.Descriptor.DisplayName}";
        AutomationProperties.SetName(this, Title);

        Heading.Text = tool.DisplayName;
        AutomationProperties.SetName(Fields, $"{tool.DisplayName} {Words}");
        ApplyKind();

        FormResults.Owner = this;
        FormResults.Windows = windows;
        FormResults.Announce = SetStatus;
        FormResults.ReadResource = (uri, label) => ReadResource?.Invoke(_connection, uri, label);

        switch (tool.Kind)
        {
            case OperationKind.Prompt:
                FormResults.ShowMessage("Result", "This prompt has not been fetched yet.",
                    "The prompt's messages appear here when it is fetched.");
                break;

            case OperationKind.Resource or OperationKind.ResourceTemplate:
                FormResults.ShowMessage("Result", "This resource has not been read yet.",
                    "What the resource holds appears here when it is read.");
                break;

            default:
                FormResults.ShowMessage("Result", "This tool has not been run yet.");
                break;
        }

        if (tool.Summary.Length > 0)
        {
            Summary.Text = tool.Summary;
            Summary.Visibility = Visibility.Visible;
        }

        OptionalToggle.Visibility = _state.HasOptional ? Visibility.Visible : Visibility.Collapsed;

        // Its own menu bar, its own place in every other window's Window menu, and Escape closes it
        // (§8). Tracked before Show, because that is what takes it out of Alt+Tab.
        _windows.Track(this);
        _windows.Attach(this, WindowMenu, closable: true);

        Bind();
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FormState.ShowOptional)) AnnounceVisibleFields();
        };

        Loaded += (_, _) =>
        {
            UpdateParametersHeading();
            SetStatus(OpeningAnnouncement());

            // A form whose only parameters are optional has nothing visible to focus, and giving
            // up leaves the caret on the window — where a screen reader says the title and stops
            // (§7.25, phase5-notes 4.6). The toggle is the useful place to land, because on such a
            // form it is the only thing there is to do.
            if (FocusField(_state.FirstToFill)) return;
            if (_state.HasOptional && OptionalToggle.Focus()) return;

            FormRunButton.Focus();
        };

        // Ctrl+Enter has to be taken before the focused control sees it. A WPF Button handles Enter
        // itself and does not look at the modifiers, so pressing Ctrl+Enter while standing on one
        // of a list's own buttons pressed the button instead of running the tool — which on an
        // object list meant it silently added another empty row (phase3-notes 4.7). The keyboard map
        // says Ctrl+Enter submits, with no exceptions for where the caret happens to be.
        // While a call runs, Escape stops it rather than closing the window: closing would stop the
        // call too, and take the form and its result pane with it. A second Escape, with nothing
        // running, closes the window as it always has (Phase 7).
        PreviewKeyDown += (_, e) =>
        {
            // Ctrl+M summarises this form's result from anywhere in the window (Phase 11), not only
            // from inside the result: found by ear, from a form field, where it did nothing at all.
            if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                FormResults.Summarise();
                return;
            }

            if (e.Key != Key.Escape || !_callInFlight || Keyboard.Modifiers != ModifierKeys.None) return;

            e.Handled = true;
            Stop();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            e.Handled = true;
            _ = RunAsync();
        };

        // Whichever of the two buttons WPF re-evaluates first, the caret ends on Stop: moved there
        // from Run if Run is still enabled, or from the window if Run has already dropped it.
        FormStopButton.IsEnabledChanged += (_, e) =>
        {
            if (e.NewValue is not true || !_toStop) return;

            _toStop = false;
            if (_callInFlight && (FormRunButton.IsKeyboardFocused || Keyboard.FocusedElement is Window or null))
                FormStopButton.Focus();
        };

        Closed += (_, _) =>
        {
            // Before the waiting is cancelled: a task carries on without this window, and the list
            // takes over its questions and says when it is done.
            if (_task is { IsFinished: false } task) Tasks?.Release(task);

            _closing.Cancel();
            FormResults.StopDescribing();
        };
    }

    /// <summary>The background task list (Phase 14), which a call that becomes a task joins.</summary>
    internal BackgroundTasks? Tasks { get; set; }

    void IStatusLine.Say(string text) => SetStatus(text);

    /// <summary>The tool this window is a form for. Used by the window manager to avoid opening two.</summary>
    public string ToolName => _tool.Name;

    /// <summary>What this window is a form for: a tool, a prompt and a resource can share a name.</summary>
    public OperationKind Kind => _tool.Kind;

    /// <summary>
    /// Reads a resource a result linked to, through the server that sent the link (Phase 13). Set by
    /// the main window, which owns the windows a read opens.
    /// </summary>
    public Action<ServerConnection, string, string>? ReadResource { get; set; }

    /// <summary>"parameters", or "arguments" for a prompt or a template — the protocol's word.</summary>
    private string Words => _tool.ArgumentWord + "s";

    private string Word => _tool.ArgumentWord;

    /// <summary>"Running", "Getting" for a prompt, "Reading" for a resource.</summary>
    private string Doing => _tool.Doing;

    /// <summary>
    /// The handful of words that differ for a prompt (Phase 12) or a resource (Phase 13). Everything
    /// else in this window is the same for all three, because each is arguments in and content out.
    /// The access keys here are the ones <see cref="AccessKeys"/> reserves for the kind (§7.21).
    /// </summary>
    private void ApplyKind()
    {
        var (menu, run, help) = _tool.Kind switch
        {
            OperationKind.Prompt => ("_Prompt", "_Get prompt",
                "Fetches the prompt's messages with what is on this form. Control plus Enter does the same."),
            OperationKind.Resource or OperationKind.ResourceTemplate => ("Re_source", "_Read resource",
                "Reads the resource this form describes. Control plus Enter does the same."),
            _ => ((string?)null, (string?)null, (string?)null),
        };

        if (menu is null) return;

        OperationMenu.Header = menu;
        RunMenuItem.Header = run;
        OptionalMenuItem.Header = "Show _optional arguments";
        OptionalToggle.Content = "Show _optional arguments";
        AutomationProperties.SetName(OptionalToggle, "Show optional arguments");
        ParametersHeading.Text = "Arguments";
        FormRunButton.Content = run;
        AutomationProperties.SetHelpText(FormRunButton, help);
    }

    /// <summary>
    /// Reads at once, for a window opened on a resource rather than on a form — a link followed from
    /// a result, where there is nothing to fill in and the user has already asked.
    /// </summary>
    public void RunWhenShown()
    {
        Loaded += (_, _) => _ = RunAsync();
    }

    /// <summary>Alt text for the images this form's calls return (Phase 10), shared with the main window.</summary>
    internal LocalModelFeatures? Describer
    {
        get => FormResults.Describer;
        set => FormResults.Describer = value;
    }

    /// <summary>The server this form calls, so the main window can find a form already open for a tool.</summary>
    public ServerConnection Connection => _connection;

    /// <summary>
    /// The server has stopped publishing this tool (<c>tools/list_changed</c>). The form stays — what
    /// is typed into it is the user's — and says so, in the title as well as the status line, because
    /// a status line is said once and a title is there every time the window is switched to.
    /// </summary>
    public void Withdrawn()
    {
        if (Title.EndsWith(" — withdrawn", StringComparison.Ordinal)) return;

        Title += " — withdrawn";
        AutomationProperties.SetName(this, Title);
        SetStatus($"{_connection.Descriptor.DisplayName} no longer publishes {_tool.DisplayName}. {Doing} it will probably fail.");
    }

    // ------------------------------------------------------------------ commands

    private void Bind()
    {
        Add(FormCommands.Submit, (_, _) => _ = RunAsync(), (_, e) => e.CanExecute = !_callInFlight);
        Add(FormCommands.Details, (_, _) => ShowDetails());
        Add(FormCommands.Stop, (_, _) => Stop(), (_, e) => e.CanExecute = _callInFlight);
        Add(FormCommands.NextPane, (_, _) => CyclePane(+1));
        Add(FormCommands.PreviousPane, (_, _) => CyclePane(-1));
        Add(FormCommands.ToggleOptional, (_, _) => ToggleOptional(), (_, e) => e.CanExecute = _state.HasOptional);

        Add(FormCommands.GoToProblem, OnGoToProblem, (_, e) => e.CanExecute = Selected(e) is ProblemEntry);

        // The list and sub-form commands are shared with the question window, which had none of
        // them for a whole manual pass and a form nobody could add to (phase5-notes 4.9).
        FormCommandBindings.Attach(this, _state.Fields, SetStatus, _windows);
    }

    private void Add(RoutedUICommand command, ExecutedRoutedEventHandler executed,
        CanExecuteRoutedEventHandler? canExecute = null)
        => CommandBindings.Add(new CommandBinding(command, executed, canExecute));

    /// <summary>
    /// Shows or hides the optional parameters, and puts the caret back where it was.
    /// </summary>
    /// <remarks>
    /// Restoring focus is the whole of the second line. Revealing the hidden fields rebuilds the
    /// list the form is bound to, which destroys the control the user was standing in — so
    /// <kbd>Ctrl</kbd>+<kbd>O</kbd> threw them out to the window, where a screen reader says the
    /// window's title and nothing else, with no way to tell what happened (phase3-notes 4.4). A
    /// field that has just been hidden cannot be returned to, so the caret goes to the first field
    /// that has to be filled in instead.
    /// </remarks>
    private void ToggleOptional()
    {
        var wasOn = FocusedField();

        _state.ShowOptional = !_state.ShowOptional;

        if (!FocusField(wasOn)) FocusField(_state.FirstToFill);
    }

    private static object? Selected(CanExecuteRoutedEventArgs e) =>
        e.Parameter is ListBox box ? box.SelectedItem : null;

    private void OnGoToProblem(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is ListBox { SelectedItem: ProblemEntry problem })
            GoTo(problem);
    }

    private void OnProblemActivated(object sender, MouseButtonEventArgs e)
    {
        if (Problems.SelectedItem is ProblemEntry problem) GoTo(problem);
    }

    private void GoTo(ProblemEntry problem)
    {
        if (FocusField(_state.Find(problem.FieldPath))) return;

        SetStatus("That problem is not about a field on this form.");
    }

    // ------------------------------------------------------------------ running

    private async Task RunAsync()
    {
        if (_callInFlight)
        {
            SetStatus(_tool.Kind == OperationKind.Tool
                ? "A tool call is already running."
                : $"{_tool.Doing} {_tool.DisplayName} has already started.");
            return;
        }

        var submission = _state.Prepare();
        ShowProblems(submission);

        // The only refusal: text that is not JSON is not a call the client can construct at all,
        // which is a different thing from a call we suspect the server will decline (§5).
        if (!submission.CanSend)
        {
            var problem = submission.Build.Problems[0];
            SetStatus($"{problem.Label}: {problem.Message}");
            FocusField(_state.Find(problem.FieldPath));
            return;
        }

        if (submission.HasIssues && Signature(submission) is { } signature && signature != _warnedAbout)
        {
            // Warn once, then do as you are told. §7.8 puts the caret on the first problem, and the
            // announcement says both what is wrong and how to overrule it — a warning with no way
            // past it is a veto wearing a hat.
            _warnedAbout = signature;

            SetStatus($"{Count(submission.Report.Issues.Count, "problem", "problems")} found. " +
                      $"{_state.LeadProblem(submission)} " +
                      "Press Control plus Enter again to run it anyway.");

            FocusField(_state.FirstProblem);
            return;
        }

        if (_tool.Safety.IsDestructive == true && !_destructiveConfirmed)
        {
            _destructiveConfirmed = true;
            SetStatus(ServerAnnouncements.Destructive(_tool, "Control plus Enter"));
            return;
        }

        _destructiveConfirmed = false;
        await SendAsync(submission);
    }

    /// <summary>
    /// Stops the running call. The server is told (the SDK sends <c>notifications/cancelled</c>), and
    /// the window stays: what was typed into it is still the user's.
    /// </summary>
    private void Stop()
    {
        // A task is stopped by asking the server (tasks/cancel), and the window goes on waiting to
        // hear that it has: cancelling the wait would only leave it running unwatched.
        if (_task is { IsFinished: false } task)
        {
            if (task.StopRequested) return;

            _ = task.StopAsync();
            SayLater($"Asked {_connection.Descriptor.DisplayName} to stop {_tool.DisplayName}. It has not stopped yet.");
            return;
        }

        if (_call is not { IsCancellationRequested: false } call) return;

        call.Cancel();
    }

    /// <summary>
    /// The server made this call a task. It joins the list, and — only if it is still running a
    /// moment later, since most tasks are quick — the window says that closing it will not stop it.
    /// </summary>
    private void OnBecameTask(BackgroundTask task)
    {
        // Listed even when it has already finished: it was a task, and the list is the record of them.
        Tasks?.Add(task, this);
        if (!_callInFlight || task.IsFinished) return;

        _task = task;

        // Later and shorter than the first version, which the result cut off for any task of two or
        // three seconds (phase14-notes): most tasks are done before this is worth saying.
        SayLater($"{_tool.DisplayName} is running in the background. Closing this window will not stop it.",
            TimeSpan.FromSeconds(3));
    }

    private async Task SendAsync(FormSubmission submission)
    {
        _callInFlight = true;
        CommandManager.InvalidateRequerySuggested();

        // Run is about to be disabled under the caret, which would drop focus to the window — heard
        // as "unavailable" and the window's title (phase14-notes, §7.25). Stop is what can be done
        // next, so the caret goes there as soon as it is enabled (see the handler in the constructor).
        _toStop = FormRunButton.IsKeyboardFocused;

        var started = DateTimeOffset.Now;
        _questions = 0;
        SetStatus(submission.Report.WasChecked
            ? $"{Doing} {_tool.DisplayName}."
            : $"{Doing} {_tool.DisplayName}. {submission.Report.Summary}");

        // Created on the UI thread, so each report is posted back to it (§4, pitfall 9).
        var throttle = new ProgressThrottle();
        var progress = new Progress<ToolProgress>(report =>
        {
            if (_callInFlight && throttle.Offer(_tool.DisplayName, report) is { } line) SetStatus(line);
        });

        try
        {
            _call = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var outcome = await _connection.CallAsync(_tool, submission.Arguments, AskAsync, progress,
                task => Dispatcher.BeginInvoke(() => OnBecameTask(task)), _call.Token);
            throttle.Finish();

            ShowResult(outcome, started);
            SetStatus(ResultFormatter.Announcement(outcome, _tool));
            _speakIfAway($"{_tool.DisplayName}: {ResultFormatter.Announcement(outcome, _tool)}");

            if (!outcome.IsError) Remember();
        }
        catch (OperationCanceledException) when (!_closing.IsCancellationRequested)
        {
            throttle.Finish();
            SetStatus(_tool.Kind != OperationKind.Tool
                ? $"Stopped {_tool.Doing.ToLowerInvariant()} {_tool.DisplayName}."
                : $"Stopped {_tool.DisplayName}. The server was told; anything it had already done stays done.");
        }
        catch (OperationCanceledException)
        {
            // The window is closing, and there is nobody left to tell.
        }
        finally
        {
            _call?.Dispose();
            _call = null;
            _task = null;
            _callInFlight = false;
            CommandManager.InvalidateRequerySuggested();

            // Stop is about to be disabled, and a disabled control that has the focus loses it to
            // the window (§7.25). Run is where the caret would go next anyway.
            if (FormStopButton.IsKeyboardFocused) FormRunButton.Focus();
        }
    }

    /// <summary>
    /// Shows a question the server asked in the middle of this call, and waits for the answer
    /// (ROADMAP §10, Phase 5).
    /// </summary>
    /// <remarks>
    /// Called on whatever thread the SDK resolved the input request on, which is why everything it
    /// touches here is either marshalled by <see cref="ElicitationWindow.AskAsync"/> or dispatched
    /// explicitly. The status line is the second kind: a window opening somewhere else is not an
    /// event a screen reader announces on the window the user was last in.
    /// </remarks>
    private async Task<ElicitationAnswer> AskAsync(ElicitationRequest request, CancellationToken cancellationToken)
    {
        // Nothing is said here at all, and that is the finding rather than an omission. The
        // window about to open announces itself, so a status line saying so is the same news twice
        // (§7's core rule) — and a status line cannot be written without being said, because NVDA
        // announces a live region when its *text* changes and not when the event is raised
        // (phase5-notes 4.8). A message that must not be spoken has no business being one.
        _questions++;

        var title = _questions > 1
            ? $"Question {_questions} — {_tool.DisplayName}"
            : $"Question — {_tool.DisplayName}";

        if (request.Kind == ElicitationKind.WebPage) title = $"A web page — {_tool.DisplayName}";

        // Windows does not always let a window come to the front by itself, so a question asked
        // while the user is in another program can open behind it, unannounced.
        await Dispatcher.InvokeAsync(() => _speakIfAway($"{_tool.DisplayName} is asking a question."));

        var answer = await ElicitationWindow.AskAsync(this, title, request, _windows, cancellationToken);

        await Dispatcher.InvokeAsync(() => SayLater(answer.Action switch
        {
            ElicitationAction.Accept => $"Answered. {_tool.DisplayName} is still running.",
            ElicitationAction.Decline => $"Declined. {_tool.DisplayName} is still running.",
            _ => $"Question dismissed. {_tool.DisplayName} is still running.",
        }));

        return answer;
    }

    /// <summary>
    /// Says something only if it is still true in a moment, and still news.
    /// </summary>
    /// <remarks>
    /// The one announcement policy this phase needed. Answering the last question of a call and the
    /// call finishing are milliseconds apart, and the two announcements collided: NVDA said
    /// "Answered. Book a journey is still running." and the completion announcement — the one that
    /// carries the result — was never heard at all (phase5-notes 4.4). "Still running" is only
    /// worth saying while it is still running, so it waits to find out.
    /// </remarks>
    private void SayLater(string text, TimeSpan? after = null)
    {
        var timer = new DispatcherTimer { Interval = after ?? TimeSpan.FromSeconds(1.2) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_callInFlight) SetStatus(text);
        };

        timer.Start();
    }

    /// <summary>
    /// Puts a result in the third pane. The heading carries whether the call failed, because an
    /// error has to survive the announcement it was first said in (§7.20, §7.24).
    /// </summary>
    private void ShowResult(ToolCallOutcome outcome, DateTimeOffset started)
    {
        _lastOutcomeAt = started;

        // Rebuilding the pane destroys whatever control the caret was in, and WPF then drops focus
        // to the window — where a screen reader says the title and nothing else. §7.25 again, in
        // the one place it had never been tried: running the tool again from the result pane
        // (phase5-notes 4.5).
        var wasReading = FormResults.IsKeyboardFocusWithin;

        FormResultHeading.Text = outcome.IsError
            ? $"Result — {_tool.DisplayName} — error"
            : $"Result — {_tool.DisplayName}";

        FormResults.Show(_tool, outcome);

        // Only when the rebuild really did lose it: putting focus back where it already is makes
        // the reader announce the control a second time.
        if ((wasReading || Keyboard.FocusedElement is Window) && !FormResults.IsKeyboardFocusWithin)
            FormResults.FocusFirst();
    }

    /// <summary>
    /// What the report says, as one string, so that pressing Run twice on the same set of problems
    /// means "yes, I meant it" and pressing it after fixing one of them warns again.
    /// </summary>
    private static string Signature(FormSubmission submission) =>
        string.Join("", submission.Report.Issues.Select(i => $"{i.FieldPath}:{i.Message}"));

    // ------------------------------------------------------------------ history

    /// <summary>
    /// Hands each text field whatever was typed into it last time (§4, pitfall 3). Done once, as
    /// the window is built, so the control a field gets does not change under the user's hands.
    /// </summary>
    private void ApplyHistory()
    {
        foreach (var field in _state.AllFields().OfType<TextFieldState>())
        {
            var values = _history.For(_connection.Descriptor.Id, HistoryKey, field.Path);
            if (values.Count > 0) field.History = values;
        }
    }

    /// <summary>
    /// Offers the server's resource addresses on a tool's parameters that want one (§4, pitfall 3's
    /// second half, Phase 13), after what this user typed there before. Fixed once, as the window is
    /// built, like history: the list a field shows does not change under the user's hands.
    /// </summary>
    private void OfferResources()
    {
        if (_tool.Kind != OperationKind.Tool) return;

        var addresses = _connection.Resources
            .Where(r => r.Kind == OperationKind.Resource)
            .Select(r => r.ResourceUri)
            .Take(ServerConnection.MaxCompletions)
            .ToList();

        if (addresses.Count == 0) return;

        foreach (var field in _state.AllFields().OfType<TextFieldState>())
        {
            if (!ResourceForm.WantsResourceUri(field.Field)) continue;

            field.History = [.. field.History.Concat(addresses).Distinct(StringComparer.Ordinal)];
            field.OffersResources = true;
        }
    }

    /// <summary>
    /// What history is filed under. A prompt's name is prefixed, because a server can publish a tool
    /// and a prompt with the same name and their fields are not the same fields. A colon cannot
    /// appear in a tool's name, so the two can never meet.
    /// </summary>
    private string HistoryKey => _tool.Kind switch
    {
        OperationKind.Prompt => "prompt:" + _tool.Name,
        OperationKind.ResourceTemplate => "template:" + _tool.Name,
        _ => _tool.Name,
    };

    private void Remember()
    {
        var recorded = false;

        foreach (var field in _state.AllFields().OfType<TextFieldState>())
        {
            if (field.Field.Kind is FieldKind.Raw or FieldKind.MultilineText) continue;
            if (!field.IsSet) continue;

            recorded |= _history.Record(_connection.Descriptor.Id, HistoryKey, field.Path, field.Text);
        }

        if (recorded) _history.Save();
    }

    // ------------------------------------------------------------------ the server's suggestions (Phase 12)

    /// <summary>What each field held from history before the server said anything, kept to go under its suggestions.</summary>
    private readonly Dictionary<TextFieldState, IReadOnlyList<string>> _past = [];

    private readonly DispatcherTimer _suggestionDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private TextFieldState? _suggestFor;
    private CancellationTokenSource? _suggesting;

    /// <summary>
    /// Puts the server's suggestions behind a prompt's arguments, on a server that answers
    /// <c>completion/complete</c> — the one place MCP can autocomplete at all (§4, pitfall 3).
    /// </summary>
    /// <remarks>
    /// Asked when a field takes the focus, and again when typing in it pauses. Nothing is announced
    /// when they arrive: the drop-down says it has values when it is opened, the field's help text says
    /// how to open it, and a status line on every pause in typing would talk over the typing.
    /// </remarks>
    private void OfferSuggestions()
    {
        // Prompts (ref/prompt) and templates (ref/resource) are the two things MCP can complete.
        if (_tool.Kind is not (OperationKind.Prompt or OperationKind.ResourceTemplate) || !_connection.SupportsCompletion)
            return;

        // A prompt's arguments are flat strings, so its top-level fields are all of them.
        foreach (var field in _state.Fields.OfType<TextFieldState>())
        {
            field.HasServerSuggestions = true;
            _past[field] = field.History;

            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TextFieldState.Text)) return;

                // Arrowing through a closed drop-down puts each value in the box in turn. Asking
                // again then would replace the list being arrowed through with one narrowed to the
                // value just reached, so a value that came from the list is not typing.
                if (field.History.Contains(field.Text, StringComparer.Ordinal)) return;

                Suggest(field);
            };
        }

        // An empty drop-down opens in silence: NVDA says "blank" and nothing else, while the field's
        // help has just promised suggestions. Found by ear on server-everything's Resource Prompt,
        // whose resource id has none until something is typed (phase12-notes). Said, not fixed by
        // hiding the drop-down, because whether the server has anything changes as the user types.
        Fields.PreviewKeyDown += (_, e) =>
        {
            var opens = (e.Key == Key.System && e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)
                        || (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None);

            if (opens && FocusedField() is TextFieldState { HasServerSuggestions: true, History.Count: 0 } field)
            {
                SetStatus(field.Text.Trim().Length == 0
                    ? $"The server has no suggestions for {field.Label} yet. Type something and try again."
                    : $"The server has no suggestions for {field.Label} that match what is typed.");
            }
        };

        Fields.GotKeyboardFocus += (_, e) =>
        {
            if ((e.NewFocus as FrameworkElement)?.DataContext is TextFieldState { HasServerSuggestions: true } field
                && !ReferenceEquals(field, _suggestFor))
            {
                Suggest(field);
            }
        };

        _suggestionDelay.Tick += async (_, _) =>
        {
            _suggestionDelay.Stop();
            if (_suggestFor is { } field) await FetchSuggestionsAsync(field);
        };

        Closed += (_, _) =>
        {
            _suggestionDelay.Stop();
            _suggesting?.Cancel();
        };
    }

    private void Suggest(TextFieldState field)
    {
        _suggestFor = field;
        _suggestionDelay.Stop();
        _suggestionDelay.Start();
    }

    private async Task FetchSuggestionsAsync(TextFieldState field)
    {
        _suggesting?.Cancel();
        var cancel = _suggesting = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);

        var typed = field.Text;

        // What the other arguments hold, so a server can narrow one by another — a repository's
        // branches once the repository is known.
        var others = _state.Fields.OfType<TextFieldState>()
            .Where(f => !ReferenceEquals(f, field) && f.Text.Trim().Length > 0)
            .ToDictionary(f => f.Field.Name, f => f.Text.Trim(), StringComparer.Ordinal);

        IReadOnlyList<string> suggestions;

        try
        {
            suggestions = await _connection.CompleteAsync(_tool, field.Field.Name, typed, others, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // An answer to a question that is no longer being asked is not shown.
        if (cancel.IsCancellationRequested || field.Text != typed) return;

        field.History = [.. suggestions.Concat(_past[field]).Distinct(StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ announcements and focus

    private string OpeningAnnouncement()
    {
        var parts = new List<string> { _tool.DisplayName };

        if (_tool.Safety.ShortDescription is { } safety) parts.Add(safety);

        // A window on a single resource has nothing to fill in, and "0 arguments, 0 required" is
        // three words for nothing.
        if (_state.Fields.Count > 0)
            parts.Add(Count(_state.Fields.Count, Word, Words) + $", {_state.RequiredCount} required");

        if (_state.HiddenCount > 0)
            parts.Add($"{_state.HiddenCount} optional and hidden. Control plus O shows them");

        return string.Join(". ", parts) + ".";
    }

    private void AnnounceVisibleFields()
    {
        UpdateParametersHeading();

        SetStatus(_state.ShowOptional
            ? $"Showing all {Count(_state.Fields.Count, Word, Words)}."
            : $"{Count(_state.HiddenCount, "optional " + Word, "optional " + Words)} hidden.");
    }

    private void UpdateParametersHeading() =>
        ParametersHeading.Text = _state.HiddenCount > 0
            ? $"{Heading2} — {_state.VisibleFields.Count} of {_state.Fields.Count}"
            : Heading2;

    private string Heading2 => _tool.Kind == OperationKind.Tool ? "Parameters" : "Arguments";

    private void ShowProblems(FormSubmission submission)
    {
        var entries = new List<ProblemEntry>();

        foreach (var problem in submission.Build.Problems)
            entries.Add(new ProblemEntry($"{problem.Label}: {problem.Message}", problem.FieldPath));

        foreach (var issue in submission.Report.Issues)
            entries.Add(new ProblemEntry(issue.Announcement, issue.FieldPath));

        Problems.ItemsSource = entries;
        ProblemsPane.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProblemsHeading.Text = $"Problems — {entries.Count}";
    }

    /// <summary>
    /// Moves the caret to a field's control. The states are what the templates bind to, so the
    /// control for one is whatever focusable element carries it as its data context — which keeps
    /// this free of any knowledge of which control a given kind became.
    /// </summary>
    /// <remarks>
    /// It settles for a field's <em>descendant</em> where the field itself has nothing focusable,
    /// which is what a group is: a <c>GroupBox</c> is deliberately not a tab stop (§7.13), so a form
    /// whose first field is an object had nowhere to send the caret and left it on the window —
    /// where the screen reader says the window's name and stops (phase3-notes 4.5). Landing on the
    /// group's first member is what the user wanted anyway.
    /// </remarks>
    private bool FocusField(FieldState? field)
    {
        if (field is null) return false;

        Fields.UpdateLayout();

        var wanted = field.SelfAndDescendants().ToHashSet();

        foreach (var element in Descendants(Fields))
        {
            if (element is not Control { Focusable: true, IsEnabled: true, Visibility: Visibility.Visible } control)
                continue;

            if (control.DataContext is not FieldState carried || !wanted.Contains(carried)) continue;

            control.BringIntoView();
            control.Focus();
            return true;
        }

        return false;
    }

    /// <summary>The field the caret is in, or null when it is somewhere that is not a field.</summary>
    private FieldState? FocusedField() =>
        (Keyboard.FocusedElement as FrameworkElement)?.DataContext as FieldState;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>
    /// Moves between the three panes. Silent on purpose: focus lands on a named control, and the
    /// screen reader announces it without help (§7's core rule, phase1-notes 2.7).
    /// </summary>
    private void CyclePane(int direction)
    {
        var panes = new List<Func<bool>>
        {
            () => FocusField(_state.VisibleFields.FirstOrDefault()),
            () => ProblemsPane.Visibility == Visibility.Visible && Focus(Problems),
            () => FormResults.FocusFirst(),
        };

        for (var attempt = 0; attempt < panes.Count; attempt++)
        {
            _paneIndex = (_paneIndex + direction + panes.Count) % panes.Count;
            if (panes[_paneIndex]()) return;
        }
    }

    private static bool Focus(Control control)
    {
        control.Focus();
        return true;
    }

    /// <summary>
    /// Puts a line in the status bar, which says it.
    /// </summary>
    /// <remarks>
    /// There is no quiet version, and Phase 5 tried to add one. A screen reader announces a polite
    /// live region when the region's <em>text</em> changes, not when the application raises the
    /// event — so a status line that is written is a status line that is spoken, and the only way
    /// not to say something is not to put it here (phase5-notes 4.8).
    /// </remarks>
    private void SetStatus(string text)
    {
        LiveRegion.Say(FormStatus, text);
    }

    private static string Count(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    // ------------------------------------------------------------------ F1

    /// <summary>
    /// Everything about whatever the caret is in — or about the tool, when it is not in a field.
    /// §4, pitfall 1: the first sentence is the help text and the rest waits behind a keystroke.
    /// </summary>
    private void ShowDetails()
    {
        // In the result pane, F1 is about the result rather than about the form behind it.
        if (FormResults.IsKeyboardFocusWithin && FormResults.Outcome is { } outcome)
        {
            TextWindow.Show(this, $"Details: {_tool.DisplayName} result",
                ResultFormatter.Render(_tool, outcome, _lastOutcomeAt), _windows);
            return;
        }

        if ((Keyboard.FocusedElement as FrameworkElement)?.DataContext is not FieldState field)
        {
            TextWindow.Show(this, $"Details: {_tool.DisplayName}", ToolDetails(), _windows);
            return;
        }

        TextWindow.Show(this, $"Details: {field.Label}", FieldDetails.Describe(field), _windows);
    }

    private string ToolDetails() => new StringBuilder()
        .AppendLine(_tool.DisplayName)
        .AppendLine(new string('-', 60))
        .AppendLine($"Name:       {_tool.Name}")
        .Append(_tool.IsResource ? $"Address:    {_tool.ResourceUri}{Environment.NewLine}" : "")
        .AppendLine($"{(_tool.Kind == OperationKind.Tool ? "Parameters:" : "Arguments: ")} {_tool.ParameterCount} ({_tool.RequiredParameterCount} required)")
        .AppendLine()
        .AppendLine("Description")
        .AppendLine(_tool.FullDescription.Length > 0 ? _tool.FullDescription : "(the server supplied none)")
        .AppendLine()
        .AppendLine(_tool.Kind == OperationKind.Tool ? "Input schema" : "Arguments, as the schema humcp built from them")
        .AppendLine(FieldDetails.Pretty(_tool.InputSchema))
        .ToString();
}

using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
public partial class ToolWindow : Window
{
    private readonly ToolInfo _tool;
    private readonly ServerConnection _connection;
    private readonly MruHistory _history;
    private readonly WindowManager _windows;
    private readonly FormState _state;
    private readonly CancellationTokenSource _closing = new();

    private string? _warnedAbout;
    private bool _callInFlight;
    private int _paneIndex;

    /// <summary>When the result on screen was asked for, for the details window (F1).</summary>
    private DateTimeOffset _lastOutcomeAt;

    public ToolWindow(
        Window owner, ToolInfo tool, ServerConnection connection, MruHistory history, WindowManager windows)
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
        _state = FormState.Create(tool.Form);

        ApplyHistory();

        Owner = owner;
        DataContext = _state;

        Title = $"{tool.DisplayName} — {connection.Descriptor.DisplayName}";
        AutomationProperties.SetName(this, Title);

        Heading.Text = tool.DisplayName;
        AutomationProperties.SetName(Fields, $"{tool.DisplayName} parameters");

        FormResults.Owner = this;
        FormResults.Windows = windows;
        FormResults.Announce = SetStatus;
        FormResults.ShowMessage("Result", "This tool has not been run yet.");

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
            FocusField(_state.FirstToFill);
        };

        // Ctrl+Enter has to be taken before the focused control sees it. A WPF Button handles Enter
        // itself and does not look at the modifiers, so pressing Ctrl+Enter while standing on one
        // of a list's own buttons pressed the button instead of running the tool — which on an
        // object list meant it silently added another empty row (phase3-notes 4.7). The keyboard map
        // says Ctrl+Enter submits, with no exceptions for where the caret happens to be.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            e.Handled = true;
            _ = RunAsync();
        };

        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>The tool this window is a form for. Used by the window manager to avoid opening two.</summary>
    public string ToolName => _tool.Name;

    // ------------------------------------------------------------------ commands

    private void Bind()
    {
        Add(FormCommands.Submit, (_, _) => _ = RunAsync(), (_, e) => e.CanExecute = !_callInFlight);
        Add(FormCommands.Details, (_, _) => ShowDetails());
        Add(FormCommands.NextPane, (_, _) => CyclePane(+1));
        Add(FormCommands.PreviousPane, (_, _) => CyclePane(-1));
        Add(FormCommands.ToggleOptional, (_, _) => ToggleOptional(), (_, e) => e.CanExecute = _state.HasOptional);

        Add(FormCommands.AddEntry, OnAddEntry, (_, e) => e.CanExecute = Entry(e) is ListFieldState { CanAdd: true });
        Add(FormCommands.RemoveEntry, OnRemoveEntry, (_, e) => e.CanExecute = Selected(e) is not null);
        Add(FormCommands.AddRow, OnAddRow);
        Add(FormCommands.EditRow, OnEditRow, (_, e) => e.CanExecute = Selected(e) is GroupFieldState);
        Add(FormCommands.GoToProblem, OnGoToProblem, (_, e) => e.CanExecute = Selected(e) is ProblemEntry);
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

    /// <summary>The state a command's parameter names, whether it arrived directly or as a list box.</summary>
    private static object? Entry(CanExecuteRoutedEventArgs e) => e.Parameter switch
    {
        ListBox box => box.DataContext,
        var other => other,
    };

    private static object? Selected(CanExecuteRoutedEventArgs e) =>
        e.Parameter is ListBox box ? box.SelectedItem : null;

    private void OnAddEntry(object sender, ExecutedRoutedEventArgs e)
    {
        var list = e.Parameter switch
        {
            ListFieldState direct => direct,
            ListBox box => box.DataContext as ListFieldState,
            _ => null,
        };

        if (list is null || !list.AddDraft()) return;

        // Said out loud because nothing else would: the entry box empties and the list grows, and
        // neither of those is an event a screen reader announces on its own.
        SetStatus($"Added to {list.Label}. {Count(list.Count, "entry", "entries")} now.");
    }

    private void OnRemoveEntry(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ListBox box) return;

        switch (box.DataContext, box.SelectedItem)
        {
            case (ListFieldState list, FieldState entry):
                var index = box.SelectedIndex;
                list.Remove(entry);
                SetStatus($"Removed from {list.Label}. {Count(list.Count, "entry", "entries")} left.");
                Reselect(box, index);
                break;

            case (ObjectListFieldState rows, GroupFieldState row):
                var position = box.SelectedIndex;
                rows.Remove(row);
                SetStatus($"Removed from {rows.Label}. {Count(rows.Count, "entry", "entries")} left.");
                Reselect(box, position);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Keeps the caret in the list after a removal. Without this the selection is lost and focus
    /// falls back to the top of the list box, which is a long way to arrow back from.
    /// </summary>
    private static void Reselect(ListBox box, int index)
    {
        if (box.Items.Count == 0) return;

        box.SelectedIndex = Math.Min(index, box.Items.Count - 1);
        box.Focus();
    }

    private void OnAddRow(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ObjectListFieldState rows) return;

        var row = rows.AddRow();
        SetStatus($"Added an entry to {rows.Label}.");
        EditGroup(rows, row);
    }

    private void OnEditRow(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ListBox { DataContext: ObjectListFieldState rows, SelectedItem: GroupFieldState row })
            return;

        EditGroup(rows, row);
    }

    private void EditGroup(ObjectListFieldState list, GroupFieldState row)
    {
        var position = list.Rows.IndexOf(row) + 1;
        FieldGroupWindow.Show(this, $"{list.Label}, entry {position}", row, _windows);
    }

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
            SetStatus("A tool call is already running.");
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
                      $"{submission.Report.Issues[0].Announcement} " +
                      "Press Control plus Enter again to run it anyway.");

            FocusField(_state.FirstProblem);
            return;
        }

        await SendAsync(submission);
    }

    private async Task SendAsync(FormSubmission submission)
    {
        _callInFlight = true;
        CommandManager.InvalidateRequerySuggested();

        var started = DateTimeOffset.Now;
        SetStatus(submission.Report.WasChecked
            ? $"Running {_tool.DisplayName}."
            : $"Running {_tool.DisplayName}. {submission.Report.Summary}");

        try
        {
            var outcome = await _connection.CallAsync(_tool, submission.Arguments, _closing.Token);

            ShowResult(outcome, started);
            SetStatus(ResultFormatter.Announcement(outcome));

            if (!outcome.IsError) Remember();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Tool call cancelled.");
        }
        finally
        {
            _callInFlight = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Puts a result in the third pane. The heading carries whether the call failed, because an
    /// error has to survive the announcement it was first said in (§7.20, §7.24).
    /// </summary>
    private void ShowResult(ToolCallOutcome outcome, DateTimeOffset started)
    {
        _lastOutcomeAt = started;

        FormResultHeading.Text = outcome.IsError
            ? $"Result — {_tool.DisplayName} — error"
            : $"Result — {_tool.DisplayName}";

        FormResults.Show(_tool, outcome);
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
            var values = _history.For(_connection.Descriptor.Id, _tool.Name, field.Path);
            if (values.Count > 0) field.History = values;
        }
    }

    private void Remember()
    {
        var recorded = false;

        foreach (var field in _state.AllFields().OfType<TextFieldState>())
        {
            if (field.Field.Kind is FieldKind.Raw or FieldKind.MultilineText) continue;
            if (!field.IsSet) continue;

            recorded |= _history.Record(_connection.Descriptor.Id, _tool.Name, field.Path, field.Text);
        }

        if (recorded) _history.Save();
    }

    // ------------------------------------------------------------------ announcements and focus

    private string OpeningAnnouncement()
    {
        var parts = new List<string> { _tool.DisplayName };

        if (_tool.Safety.ShortDescription is { } safety) parts.Add(safety);

        parts.Add(Count(_state.Fields.Count, "parameter", "parameters") +
                  $", {_state.RequiredCount} required");

        if (_state.HiddenCount > 0)
            parts.Add($"{_state.HiddenCount} optional and hidden. Control plus O shows them");

        return string.Join(". ", parts) + ".";
    }

    private void AnnounceVisibleFields()
    {
        UpdateParametersHeading();

        SetStatus(_state.ShowOptional
            ? $"Showing all {Count(_state.Fields.Count, "parameter", "parameters")}."
            : $"{Count(_state.HiddenCount, "optional parameter", "optional parameters")} hidden.");
    }

    private void UpdateParametersHeading() =>
        ParametersHeading.Text = _state.HiddenCount > 0
            ? $"Parameters — {_state.VisibleFields.Count} of {_state.Fields.Count}"
            : "Parameters";

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

    private void SetStatus(string text)
    {
        FormStatus.Text = text;

        var peer = UIElementAutomationPeer.FromElement(FormStatus)
                   ?? UIElementAutomationPeer.CreatePeerForElement(FormStatus);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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

        var text = new StringBuilder()
            .AppendLine(field.Label)
            .AppendLine(new string('-', 60))
            .AppendLine($"Sent as:     {(field.Path.Length == 0 ? "(the whole argument object)" : field.Path)}")
            .AppendLine($"Kind:        {field.Field.Kind}")
            .AppendLine($"Tier:        {(int)field.Field.Tier} ({field.Field.Tier})")
            .AppendLine($"Required:    {(field.IsRequired ? "yes" : "no")}")
            .AppendLine($"Empty means: {(field.Field.AllowsNull ? "null" : "this parameter is not sent")}")
            .AppendLine();

        if (field.Field.DegradedReason is { Length: > 0 } reason)
            text.AppendLine($"Shown as JSON because the schema describes {reason}.").AppendLine();

        if (!field.Field.Constraints.IsEmpty)
            text.AppendLine("Limits").AppendLine(field.Field.Constraints.Describe()).AppendLine();

        if (field.Status is { Length: > 0 } status)
            text.AppendLine("Problem").AppendLine(status).AppendLine();

        text.AppendLine("Description")
            .AppendLine(field.Field.FullDescription.Length > 0
                ? field.Field.FullDescription
                : "(the server supplied none)")
            .AppendLine()
            .AppendLine("Schema")
            .AppendLine(Pretty(field.Field.Schema));

        TextWindow.Show(this, $"Details: {field.Label}", text.ToString(), _windows);
    }

    private string ToolDetails() => new StringBuilder()
        .AppendLine(_tool.DisplayName)
        .AppendLine(new string('-', 60))
        .AppendLine($"Name:       {_tool.Name}")
        .AppendLine($"Parameters: {_tool.ParameterCount} ({_tool.RequiredParameterCount} required)")
        .AppendLine()
        .AppendLine("Description")
        .AppendLine(_tool.FullDescription.Length > 0 ? _tool.FullDescription : "(the server supplied none)")
        .AppendLine()
        .AppendLine("Input schema")
        .AppendLine(Pretty(_tool.InputSchema))
        .ToString();

    private static string Pretty(JsonElement schema)
    {
        try
        {
            return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "(could not be formatted: " + ex.Message + ")";
        }
    }
}

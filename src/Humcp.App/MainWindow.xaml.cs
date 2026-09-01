using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Humcp.Core;
using Humcp.Speech;

namespace Humcp.App;

/// <summary>
/// Phase 1's walking skeleton (ROADMAP §10): one hard-coded stdio server, its tools in a real
/// tree, a zero-argument call, and the text result in a read-only box — all of it reachable from
/// the keyboard alone.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly RoutedCommand NextPaneCommand = new();
    public static readonly RoutedCommand PreviousPaneCommand = new();
    public static readonly RoutedCommand DetailsCommand = new();
    public static readonly RoutedCommand ReconnectCommand = new();

    private readonly ObservableCollection<CatalogueNode> _roots = [];
    private readonly List<string> _speechLog = [];
    private readonly List<ToolWindow> _forms = [];
    private readonly WindowManager _windows = new();
    private readonly IAnnouncer _announcer;
    private readonly ServerConnection _connection;
    private readonly MruHistory _history = MruHistory.Load();
    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource? _currentCall;
    private bool _callInFlight;
    private int _paneIndex;

    /// <summary>What is in the result pane, for the details window (F1) to write out in full.</summary>
    private ToolInfo? _lastTool;
    private DateTimeOffset _lastCallAt;

    public MainWindow()
    {
        InitializeComponent();

        _announcer = PrismAnnouncer.Create(line =>
        {
            lock (_speechLog) _speechLog.Add(line);
        });

        // prism delivers these on a background thread (phase0-findings §4), so they are marshalled
        // before anything touches the UI.
        _announcer.AvailabilityChanged += message =>
            Dispatcher.BeginInvoke(() => SetStatus(message));

        _connection = new ServerConnection(BuiltInServers.Default());

        CommandBindings.Add(new CommandBinding(NextPaneCommand, (_, _) => CyclePane(+1)));
        CommandBindings.Add(new CommandBinding(PreviousPaneCommand, (_, _) => CyclePane(-1)));
        CommandBindings.Add(new CommandBinding(DetailsCommand, (_, _) => ShowDetails()));
        CommandBindings.Add(new CommandBinding(ReconnectCommand, (_, _) => _ = ConnectAsync()));

        Tree.ItemsSource = _roots;
        UpdateRunAvailability();

        // The view opens whatever windows its parts need, and says what it did through the one
        // live region this window owns (§7's core rule). Its automation id differs from the form
        // window's on purpose: an owned window is a UIA *descendant* of this one, so an id shared
        // between them makes every lookup from here a coin toss (§9, phase3-notes 4.3).
        MainResults.Owner = this;
        MainResults.Windows = _windows;
        MainResults.Announce = SetStatus;
        MainResults.ShowMessage("Result", "No tool has been run yet.");

        // The main window keeps its place in the task bar; everything humcp opens beside it does
        // not, and every window carries the same Window menu (§8).
        _windows.TrackMain(this);
        _windows.Attach(this, WindowMenu, closable: false);

        Loaded += async (_, _) =>
        {
            Tree.Focus();
            await ConnectAsync();
        };

        Closed += (_, _) =>
        {
            _shutdown.Cancel();

            foreach (var form in _forms.ToList()) form.Close();

            _history.Save();
            _ = _connection.DisposeAsync();
            _announcer.Dispose();
        };
    }

    // ------------------------------------------------------------------ connection

    private async Task ConnectAsync()
    {
        RebuildTree();
        SetStatus($"Connecting to {_connection.Descriptor.DisplayName}.");

        try
        {
            await _connection.ConnectAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RebuildTree();
        SelectFirstNode();
        UpdateRunAvailability();

        var snapshot = _connection.Snapshot();
        SetStatus(snapshot.State switch
        {
            ServerConnectionState.Connected =>
                $"Connected to {snapshot.DisplayName}. {snapshot.Tools.Count} tools, " +
                $"protocol {snapshot.NegotiatedProtocolVersion}.",
            ServerConnectionState.Failed =>
                $"Could not connect to {snapshot.DisplayName}. {snapshot.FailureReason}",
            _ => $"{snapshot.DisplayName} is not connected.",
        });
    }

    private void RebuildTree()
    {
        // The tree is immutable and rebuilt wholesale (see CatalogueNode), which at this size costs
        // nothing and means the UI never binds to a half-updated node.
        var selectedToolName = SelectedTool()?.Name;

        _roots.Clear();
        _roots.Add(ToolTreeBuilder.BuildServerNode(_connection.Snapshot()));

        Tree.UpdateLayout();
        if (selectedToolName is not null) SelectToolByName(selectedToolName);
    }

    // ------------------------------------------------------------------ running a tool

    private async Task RunSelectedToolAsync()
    {
        var tool = SelectedTool();
        if (tool is null)
        {
            SetStatus("Select a tool in the tree first.");
            return;
        }

        // A tool with parameters gets a form window; a tool with none is a call, and making the
        // user open a window with nothing on it to press one more button would be ceremony.
        if (tool.ParameterCount > 0)
        {
            OpenForm(tool);
            return;
        }

        if (_callInFlight)
        {
            SetStatus("A tool call is already running.");
            return;
        }

        _callInFlight = true;
        var questions = 0;
        UpdateRunAvailability();
        SetStatus($"Running {tool.DisplayName}.");

        var started = DateTimeOffset.Now;
        _currentCall = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        try
        {
            // Nothing here blocks the UI thread: the window stays responsive, which is what the
            // fixture's two-second tool is for (ROADMAP §4, pitfall 9).
            var outcome = await _connection.CallAsync(tool, null, AskAsync, _currentCall.Token);

            async Task<ElicitationAnswer> AskAsync(ElicitationRequest request, CancellationToken token)
            {
                // A tool with no parameters of its own can still stop to ask for some, which is
                // most of what MRTR is for: mrtr_confirm takes no arguments and asks a question.
                //
                // Nothing is announced here: the window about to open announces itself, and a
                // status line cannot be written without being spoken (phase5-notes 4.8).
                questions++;

                var title = request.Kind == ElicitationKind.WebPage
                    ? $"A web page — {tool.DisplayName}"
                    : questions > 1
                        ? $"Question {questions} — {tool.DisplayName}"
                        : $"Question — {tool.DisplayName}";

                return await ElicitationWindow.AskAsync(this, title, request, _windows, token);
            }

            ShowResult(tool, outcome, started);
            SetStatus(ResultFormatter.Announcement(outcome));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Tool call cancelled.");
        }
        finally
        {
            _currentCall?.Dispose();
            _currentCall = null;
            _callInFlight = false;
            UpdateRunAvailability();
        }
    }

    /// <summary>
    /// Puts a result in the pane. The heading says which tool and whether it failed, because an
    /// error is a fact that has to survive the announcement it was first said in (§7.20, §7.24).
    /// </summary>
    private void ShowResult(ToolInfo tool, ToolCallOutcome outcome, DateTimeOffset started)
    {
        _lastTool = tool;
        _lastCallAt = started;

        // Rebuilding the pane destroys whatever control the caret was in, and WPF then drops focus
        // to the window (§7.25, phase5-notes 4.5).
        var wasReading = MainResults.IsKeyboardFocusWithin;

        ResultHeading.Text = outcome.IsError
            ? $"Result — {tool.DisplayName} — error"
            : $"Result — {tool.DisplayName}";

        MainResults.Show(tool, outcome);

        if ((wasReading || Keyboard.FocusedElement is Window) && !MainResults.IsKeyboardFocusWithin)
            MainResults.FocusFirst();
    }

    /// <summary>
    /// Opens the form for a tool, or brings the one already open to the front. Non-modal (§8), so
    /// several tools can be part-filled at once and the tree stays usable behind them.
    /// </summary>
    private void OpenForm(ToolInfo tool)
    {
        if (_forms.FirstOrDefault(f => f.ToolName == tool.Name) is { } existing)
        {
            WindowManager.Activate(existing);
            SetStatus($"{tool.DisplayName} is already open.");
            return;
        }

        var form = new ToolWindow(this, tool, _connection, _history, _windows);

        _forms.Add(form);
        form.Closed += (_, _) => _forms.Remove(form);
        form.Show();
    }

    private void UpdateRunAvailability()
    {
        var tool = SelectedTool();
        var runnable = tool is not null && (tool.ParameterCount > 0 || !_callInFlight);

        RunButton.IsEnabled = runnable;
        RunMenuItem.IsEnabled = runnable;
    }

    // ------------------------------------------------------------------ selection helpers

    private ToolInfo? SelectedTool() => (Tree.SelectedItem as CatalogueNode)?.Tool;

    private void SelectFirstNode()
    {
        Tree.UpdateLayout();
        if (Tree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem item) return;

        item.IsSelected = true;
        item.BringIntoView();
    }

    private void SelectToolByName(string name)
    {
        foreach (var (node, container) in EnumerateContainers(Tree, _roots))
        {
            if (node.Tool?.Name != name) continue;
            container.IsSelected = true;
            container.BringIntoView();
            return;
        }
    }

    private static IEnumerable<(CatalogueNode Node, TreeViewItem Container)> EnumerateContainers(
        ItemsControl parent, IEnumerable<CatalogueNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem container) continue;

            yield return (node, container);

            container.UpdateLayout();
            foreach (var descendant in EnumerateContainers(container, node.Children))
                yield return descendant;
        }
    }

    // ------------------------------------------------------------------ panes and announcements

    /// <summary>
    /// Moves focus between panes. Deliberately silent: focus lands on a named control whose name
    /// <em>is</em> the pane name, and the screen reader announces that on its own. Anything said
    /// here would be the second time the user heard it.
    /// </summary>
    private void CyclePane(int direction)
    {
        Func<bool>[] panes = [FocusTree, () => MainResults.FocusFirst()];

        for (var attempt = 0; attempt < panes.Length; attempt++)
        {
            _paneIndex = (_paneIndex + direction + panes.Length) % panes.Length;
            if (panes[_paneIndex]()) return;
        }
    }

    /// <summary>
    /// Puts the caret in the tree, and answers whether it arrived.
    /// </summary>
    /// <remarks>
    /// <c>TreeView.Focus()</c> answers <b>false</b> once the tree has items — focus belongs to the
    /// selected <c>TreeViewItem</c>, which is the same fact §9 records about focusing a tree from a
    /// test. Read as a pane's success it means the cycle skips straight past the tree to the result
    /// and never comes back, which is what F6 did for the length of one manual pass: the tree was
    /// announced, and then the caret was somewhere else. Ask where the focus ended up instead.
    /// </remarks>
    private bool FocusTree()
    {
        Tree.Focus();
        return Tree.IsKeyboardFocusWithin;
    }

    /// <summary>
    /// Sets the status line and raises a live-region event. This is the app's only announcement
    /// channel, and it is pure UI Automation — no prism.
    /// </summary>
    /// <remarks>
    /// Settled by ear, which is the only way it could be settled (phase0-findings §6, checklist
    /// item 11; phase1-notes 2.7). A polite live region reaches NVDA on its own, so speaking the
    /// same string through prism as well means every completion is heard twice. prism is for text
    /// that has no on-screen representation at all — of which Phase 1 has none.
    /// </remarks>
    /// <summary>
    /// Puts a line in the status bar, which says it. There is no quiet version — a reader announces
    /// a live region when its text changes, not when the event is raised (phase5-notes 4.8).
    /// </summary>
    private void SetStatus(string text)
    {
        LiveStatus.Text = text;

        var peer = UIElementAutomationPeer.FromElement(LiveStatus)
                   ?? UIElementAutomationPeer.CreatePeerForElement(LiveStatus);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ------------------------------------------------------------------ event handlers

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Deliberately silent. The tree is nouns, and UI Automation already reads the selected
        // node; speaking here would double every arrow press (§7).
        UpdateRunAvailability();
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (SelectedTool() is null) return;

        e.Handled = true;
        _ = RunSelectedToolAsync();
    }

    private void OnRun(object sender, RoutedEventArgs e) => _ = RunSelectedToolAsync();

    private void OnReconnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private void OnDetails(object sender, RoutedEventArgs e) => ShowDetails();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// ROADMAP §4, pitfall 1: the first sentence of a description is the help text, and the whole
    /// thing — however many pages of it a server ships — is here, on demand.
    /// </summary>
    private void ShowDetails()
    {
        // In the result pane, F1 is about the result: every part written out, the structured
        // content as JSON, and the schema it was read against. The rendered result is what is easy
        // to read; this is what is complete, and it is one keystroke away from it.
        if (MainResults.IsKeyboardFocusWithin && MainResults.Outcome is { } outcome)
        {
            TextWindow.Show(this, $"Details: {_lastTool?.DisplayName ?? "result"}",
                ResultFormatter.Render(_lastTool, outcome, _lastCallAt), _windows);
            return;
        }

        if (Tree.SelectedItem is not CatalogueNode node)
        {
            SetStatus("Nothing is selected in the tree.");
            return;
        }

        if (node.Tool is not { } tool)
        {
            TextWindow.Show(this, $"Details: {node.AccessibleName}", node.AccessibleName, _windows);
            return;
        }

        var text = new StringBuilder()
            .AppendLine(tool.DisplayName)
            .AppendLine(new string('-', 60))
            .AppendLine($"Name:       {tool.Name}")
            .AppendLine($"Parameters: {tool.ParameterCount} ({tool.RequiredParameterCount} required)")
            .AppendLine($"Read-only:  {Describe(tool.Safety.IsReadOnly)}")
            .AppendLine($"Destructive:{Describe(tool.Safety.IsDestructive)}")
            .AppendLine($"Idempotent: {Describe(tool.Safety.IsIdempotent)}")
            .AppendLine()
            .AppendLine("Description")
            .AppendLine(tool.FullDescription.Length > 0 ? tool.FullDescription : "(the server supplied none)")
            .AppendLine()
            .AppendLine("Input schema")
            .AppendLine(Pretty(tool.InputSchema))
            .ToString();

        TextWindow.Show(this, $"Details: {tool.DisplayName}", text, _windows);
    }

    private void OnSpeechDiagnostics(object sender, RoutedEventArgs e)
    {
        string log;
        lock (_speechLog) log = string.Join(Environment.NewLine, _speechLog.TakeLast(200));

        var text = new StringBuilder()
            .AppendLine(_announcer.Diagnostics)
            .AppendLine()
            .AppendLine("prism log:")
            .AppendLine(log.Length == 0 ? "  (no messages)" : log)
            .ToString();

        TextWindow.Show(this, "Speech diagnostics", text, _windows);
    }

    private void OnConnectionDiagnostics(object sender, RoutedEventArgs e)
    {
        var snapshot = _connection.Snapshot();
        var diagnostics = _connection.Diagnostics;

        var text = new StringBuilder()
            .AppendLine($"Server:      {snapshot.DisplayName}")
            .AppendLine($"Command:     {_connection.Descriptor.CommandLine}")
            .AppendLine($"State:       {snapshot.State}")
            .AppendLine($"Protocol:    {snapshot.NegotiatedProtocolVersion ?? "(not negotiated)"}")
            .AppendLine($"Server said its name is: {_connection.ServerTitle ?? "(nothing yet)"}")
            .AppendLine($"Tools:       {snapshot.Tools.Count}")
            .AppendLine($"Failure:     {snapshot.FailureReason ?? "(none)"}")
            .AppendLine()
            .AppendLine("Standard error from the server process:")
            .AppendLine(diagnostics.Count == 0 ? "  (nothing)" : string.Join(Environment.NewLine, diagnostics))
            .AppendLine()
            .AppendLine($"Override this server with {BuiltInServers.CommandVariable} and " +
                        $"{BuiltInServers.ArgumentsVariable}.")
            .ToString();

        TextWindow.Show(this, "Connection diagnostics", text, _windows);
    }

    private static string Describe(bool? hint) => hint switch
    {
        true => " yes (a server-supplied hint, never proof)",
        false => " no (a server-supplied hint, never proof)",
        null => " not stated",
    };

    private static string Pretty(JsonElement schema)
    {
        try
        {
            return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return "(could not be formatted: " + ex.Message + ")";
        }
    }
}

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

    /// <summary>How long a typed run stays one search, matching the shell's own type-ahead.</summary>
    private static readonly TimeSpan TypeAheadWindow = TimeSpan.FromSeconds(1);

    private string _prefix = "";
    private DateTime _prefixAt;

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
        UpdateRunAvailability();
        SetStatus($"Running {tool.DisplayName}.");

        var started = DateTimeOffset.Now;
        _currentCall = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        try
        {
            // Nothing here blocks the UI thread: the window stays responsive, which is what the
            // fixture's two-second tool is for (ROADMAP §4, pitfall 9).
            var outcome = await _connection.CallAsync(tool, _currentCall.Token);

            var text = ResultFormatter.Render(tool.DisplayName, outcome, started);
            ShowResult($"Result — {tool.DisplayName}", text);
            SetStatus(ResultFormatter.Announcement(outcome, text));
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

    private void ShowResult(string heading, string text)
    {
        ResultHeading.Text = heading;
        AutomationProperties.SetName(ResultBox, heading);
        ResultBox.Text = text;
        ResultBox.CaretIndex = 0;
        ResultBox.ScrollToHome();
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

    /// <summary>
    /// First-letter navigation: typing a letter moves to the next node whose name begins with it,
    /// and typing several in quick succession matches the whole run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by hand because WPF's <c>TreeView</c> does not have it. A list box and a combo box
    /// both do — it is <c>TextSearch</c>, and it is part of what the Windows accessibility contract
    /// assumes of every list-shaped control — but the tree simply drops the keystroke, which a
    /// manual pass found by pressing S on a nine-tool server and going nowhere (phase3-notes 4.10).
    /// </para>
    /// <para>
    /// This tree is the application's primary navigation, and it is sized by whatever a server
    /// publishes: on a forty-tool server, arrowing is forty presses and listening to forty names.
    /// The filter box of §8 is a better answer and is Phase 6's; this is the one every other tree on
    /// the system already has.
    /// </para>
    /// </remarks>
    private void OnTreeTextInput(object sender, TextCompositionEventArgs e)
    {
        var typed = e.Text;
        if (typed.Length == 0 || char.IsControl(typed[0])) return;

        var now = DateTime.UtcNow;
        _prefix = now - _prefixAt > TypeAheadWindow ? typed : _prefix + typed;
        _prefixAt = now;

        // A repeated single letter means "the next one starting with it" rather than a literal run
        // of that letter, which is how every other list on Windows behaves.
        var target = _prefix.Length > 1 && _prefix.All(c => c == _prefix[0]) ? _prefix[..1] : _prefix;

        if (Select(target)) e.Handled = true;
    }

    private bool Select(string prefix)
    {
        var nodes = EnumerateContainers(Tree, _roots).ToList();
        if (nodes.Count == 0) return false;

        // From just after the current selection, wrapping, so repeated presses cycle the matches.
        var start = nodes.FindIndex(n => n.Container.IsSelected) + 1;

        for (var i = 0; i < nodes.Count; i++)
        {
            var (node, container) = nodes[(start + i) % nodes.Count];

            if (!node.AccessibleName.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) continue;

            container.IsSelected = true;
            container.BringIntoView();
            container.Focus();
            return true;
        }

        return false;
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
        Action[] panes = [() => Tree.Focus(), () => ResultBox.Focus()];

        _paneIndex = (_paneIndex + direction + panes.Length) % panes.Length;
        panes[_paneIndex]();
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

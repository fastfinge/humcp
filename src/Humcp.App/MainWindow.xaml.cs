using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Humcp.Core;
using Humcp.Schema;
using Humcp.Speech;

namespace Humcp.App;

/// <summary>
/// The main window: every configured server and its tools in one tree, and the result of whatever
/// was last run from it (ROADMAP §8). Servers come from <c>servers.json</c> through a
/// <see cref="ServerRegistry"/> since Phase 6.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly RoutedCommand NextPaneCommand = new();
    public static readonly RoutedCommand PreviousPaneCommand = new();
    public static readonly RoutedCommand DetailsCommand = new();
    public static readonly RoutedCommand ReconnectCommand = new();
    public static readonly RoutedCommand AddServerCommand = new();
    public static readonly RoutedCommand EditServerCommand = new();
    public static readonly RoutedCommand SettingsCommand = new();
    public static readonly RoutedCommand FindCommand = new();

    /// <summary>What the tree says when there is nothing in it, which is every first run.</summary>
    private const string NoServers =
        "No servers are configured. Control plus N adds one; Import servers on the File menu reads another client's configuration.";

    private readonly ObservableCollection<CatalogueNode> _roots = [];
    private readonly List<string> _speechLog = [];
    private readonly List<ToolWindow> _forms = [];
    private readonly WindowManager _windows = new();
    private readonly IAnnouncer _announcer;
    private readonly ServerRegistry _registry;
    private readonly MruHistory _history = MruHistory.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Servers whose node is waiting to be rebuilt, coalesced into one pass on the UI thread.</summary>
    private readonly HashSet<ServerConnection> _stale = [];

    /// <summary>What the tree last showed for each server, so a change can be said as a change.</summary>
    private readonly Dictionary<ServerConnection, ServerSnapshot> _shown = [];

    private CancellationTokenSource? _currentCall;
    private bool _callInFlight;
    private int _paneIndex;

    /// <summary>
    /// The server a first Delete was pressed on. Removing one loses its configuration, environment
    /// variables and all, so it is asked twice — the warn-once-then-do-it a form already uses (§5),
    /// rather than a modal dialog in an application that has none.
    /// </summary>
    private ServerConnection? _removing;

    /// <summary>
    /// A destructive tool whose first Enter has warned (ROADMAP §3). The second runs it; moving away
    /// is changing your mind, the same as <see cref="_removing"/>.
    /// </summary>
    private ToolInfo? _confirming;

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

        _registry = new ServerRegistry(ServerConfiguration.Load());

        // Raised on whatever thread the connection changed on. Marshalled, and coalesced: a server
        // starting goes through two states in a few milliseconds and one rebuild covers both.
        _registry.ServerChanged += (_, connection) =>
        {
            bool first;
            lock (_stale) first = _stale.Add(connection) && _stale.Count == 1;

            if (first) Dispatcher.BeginInvoke(RefreshStale);
        };

        CommandBindings.Add(new CommandBinding(NextPaneCommand, (_, _) => CyclePane(+1)));
        CommandBindings.Add(new CommandBinding(PreviousPaneCommand, (_, _) => CyclePane(-1)));
        CommandBindings.Add(new CommandBinding(DetailsCommand, (_, _) => ShowDetails()));
        CommandBindings.Add(new CommandBinding(ReconnectCommand, (_, _) => _ = ReconnectSelectedAsync()));
        CommandBindings.Add(new CommandBinding(AddServerCommand, (_, _) => AddServer()));
        CommandBindings.Add(new CommandBinding(EditServerCommand, (_, _) => EditServer()));
        CommandBindings.Add(new CommandBinding(SettingsCommand, (_, _) => OpenSettings()));
        CommandBindings.Add(new CommandBinding(FindCommand, (_, _) => FocusSearch()));

        // Sized from the screen rather than from a constant (PaneLayout): the person most likely to
        // be at the keyboard is the one who cannot see whether the window suits the screen.
        var area = SystemParameters.WorkArea;
        (Width, Height) = PaneLayout.MainWindow(area.Width, area.Height, 800, 560);

        if (_settings.TreePaneWidth is { } width) TreeColumn.Width = new GridLength(width);

        // A drag is somebody choosing a width on purpose, so it is kept and fitting stops.
        Splitter.DragCompleted += (_, _) => RememberPaneWidth();

        SizeChanged += (_, _) => FitPanesSoon();

        // Escape stops a call run from the tree. The main window has nothing else for it to do: it
        // is the one window Escape does not close (§8).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_callInFlight || Keyboard.Modifiers != ModifierKeys.None) return;

            e.Handled = true;
            _currentCall?.Cancel();
        };

        Tree.ItemsSource = _roots;
        // Present before it is asked for, for the reason the result grid's is (phase6-notes).
        Tree.ContextMenu = new ContextMenu();
        Tree.ContextMenuOpening += OnTreeContextMenuOpening;
        SearchResults.ContextMenu = new ContextMenu();
        SearchResults.ContextMenuOpening += OnTreeContextMenuOpening;
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
            RebuildTree();
            SelectFirstNode();
            FocusTree();
            await ConnectAllAsync();
        };

        Closed += (_, _) =>
        {
            _shutdown.Cancel();

            foreach (var form in _forms.ToList()) form.Close();

            _history.Save();
            _ = _registry.DisposeAsync();
            _announcer.Dispose();
        };
    }

    // ------------------------------------------------------------------ connection

    /// <summary>
    /// Starts every enabled server and says one sentence about all of them when they are done —
    /// not one per server, which at start-up is five interruptions and the last one wins.
    /// </summary>
    private async Task ConnectAllAsync()
    {
        var configuration = _registry.Configuration;

        if (configuration.LastError is { } error)
        {
            SetStatus(error);
            return;
        }

        if (_registry.Connections.Count == 0)
        {
            SetStatus(NoServers);
            return;
        }

        var enabled = _registry.Connections.Where(c => c.Descriptor.Enabled).ToList();

        SetStatus(enabled.Count switch
        {
            0 => "Every configured server is disabled.",
            1 => $"Connecting to {enabled[0].Descriptor.DisplayName}.",
            var n => $"Connecting to {n} servers.",
        });

        if (enabled.Count == 0) return;

        try
        {
            await _registry.ConnectAllAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RefreshStale();
        UpdateRunAvailability();

        var summary = ServerAnnouncements.Startup(_registry.Snapshots());

        // Entries the file holds and humcp could not read are said once, here, rather than lost: a
        // server that is silently not in the tree is one the user goes looking for.
        if (configuration.Problems.Count > 0)
        {
            summary += configuration.Problems.Count == 1
                ? $" One entry in servers.json could not be read. {configuration.Problems[0]}"
                : $" {configuration.Problems.Count} entries in servers.json could not be read. Connection diagnostics lists them.";
        }

        if (summary.Length > 0)
        {
            SetStatus(summary.Trim());
            SpeakIfAway(summary.Trim());
        }
    }

    /// <summary>Restarts the server the selection is in, and says how that went.</summary>
    private async Task ReconnectSelectedAsync()
    {
        if (SelectedServer() is not { } connection)
        {
            SetStatus("Select a server in the tree first.");
            return;
        }

        SetStatus($"Connecting to {connection.Descriptor.DisplayName}.");

        try
        {
            await connection.ConnectAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RefreshStale();
        UpdateRunAvailability();
        SetStatus(ServerAnnouncements.Connection(connection.Snapshot()));
    }

    /// <summary>
    /// Removes the selected server, on the second asking. The first says exactly what will go,
    /// because what goes includes environment variables that may have been an API key typed once.
    /// </summary>
    private void RemoveSelected()
    {
        if (SelectedServer() is not { } connection)
        {
            SetStatus("Select a server in the tree first.");
            return;
        }

        var name = connection.Descriptor.DisplayName;

        if (!ReferenceEquals(_removing, connection))
        {
            _removing = connection;
            SetStatus($"Press Delete again to remove {name} and its settings from humcp.");
            return;
        }

        _removing = null;

        foreach (var form in _forms.Where(f => ReferenceEquals(f.Connection, connection)).ToList()) form.Close();

        if (!_registry.Remove(connection))
        {
            SetStatus(_registry.Configuration.LastError ?? $"{name} could not be removed.");
            return;
        }

        RebuildTree();
        SelectFirstNode();
        FocusTree();
        UpdateRunAvailability();
        SetStatus($"Removed {name}.");
    }

    // ------------------------------------------------------------------ adding, editing, importing

    /// <summary>
    /// Opens the add-a-server form: humcp's own settings in a form its own generator built
    /// (<see cref="ServerForm"/>). The server is saved when the form closes and started after, so
    /// the form never waits for a download.
    /// </summary>
    private void AddServer()
    {
        OpenOwnForm("Add a server", "AddServer", ServerForm.Model, "Save",
            state => ServerForm.Fill(state, null),
            state =>
            {
                if (ServerForm.Read(state, "", null, out var problem) is not { } server) return problem;

                server = server with { Id = ServerForm.NewId(_registry.Configuration, server.DisplayName) };

                if (_registry.Add(server) is not { } connection)
                    return _registry.Configuration.LastError ?? "The server could not be saved.";

                Adopt([connection], $"Added {server.DisplayName}.");
                return null;
            });
    }

    /// <summary>Opens the selected server's settings, filled in with what it has now.</summary>
    private void EditServer()
    {
        if (SelectedServer() is not { } connection)
        {
            SetStatus("Select a server in the tree first.");
            return;
        }

        var current = connection.Descriptor;

        OpenOwnForm($"Settings for {current.DisplayName}", "EditServer", ServerForm.Model, "Save",
            state => ServerForm.Fill(state, current),
            async state =>
            {
                if (ServerForm.Read(state, current.Id, current, out var problem) is not { } server) return problem;

                // A form left open while the server was removed some other way has nothing to save to.
                if (IndexOf(_registry.Connections, connection) < 0) return $"{current.DisplayName} has been removed.";

                if (!await _registry.UpdateAsync(connection, server))
                    return _registry.Configuration.LastError ?? "The settings could not be saved.";

                Adopt([connection], $"Saved {server.DisplayName}.");
                return null;
            });
    }

    /// <summary>
    /// Opens the paste-a-configuration form. Whatever another client's documentation says to copy
    /// is accepted (<see cref="ServerConfiguration.Parse"/>), and every server in it is added.
    /// </summary>
    private void ImportServers()
    {
        OpenOwnForm("Import servers", "ImportServers", ServerForm.ImportModel, "Import", null,
            state =>
            {
                var text = (state.Find(ServerForm.ImportField) as TextFieldState)?.Text;
                var import = ServerConfiguration.Parse(text);

                if (import.Servers.Count == 0) return string.Join(" ", import.Problems);

                var added = new List<ServerConnection>();

                foreach (var server in import.Servers)
                {
                    if (_registry.Add(server) is not { } connection)
                    {
                        if (added.Count == 0) return _registry.Configuration.LastError ?? "The servers could not be saved.";
                        break;
                    }

                    added.Add(connection);
                }

                var names = ServerAnnouncements.List(added.Select(c => c.Descriptor.DisplayName));
                var message = added.Count == 1 ? $"Imported {names}." : $"Imported {added.Count} servers: {names}.";

                if (import.Problems.Count > 0)
                {
                    message += import.Problems.Count == 1
                        ? $" One entry was left out. {import.Problems[0]}"
                        : $" {import.Problems.Count} entries were left out. {import.Problems[0]}";
                }

                Adopt(added, message);
                return null;
            });
    }

    private void OpenOwnForm(
        string title, string prefix, FormModel form, string saveLabel,
        Action<FormState>? fill, Func<FormState, string?> save)
        => OpenOwnForm(title, prefix, form, saveLabel, fill, state => Task.FromResult(save(state)));

    private void OpenOwnForm(
        string title, string prefix, FormModel form, string saveLabel,
        Action<FormState>? fill, Func<FormState, Task<string?>> save)
    {
        // One of each: a second Add server while the first is open brings the first to the front,
        // the way a second Enter on a tool does.
        if (_windows.Windows.OfType<OwnFormWindow>().FirstOrDefault(w => w.Title == title) is { } open)
        {
            WindowManager.Activate(open);
            return;
        }

        var window = new OwnFormWindow(this, title, prefix, form, saveLabel, _windows, fill, save);

        // The tree was rebuilt while the form was in front, so the item the caret left from may no
        // longer exist, and WPF would put focus on the window (§7.25).
        window.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsActive && Keyboard.FocusedElement is null or Window) FocusTree();
        });

        window.Show();
    }

    /// <summary>humcp's own preferences, in a form generated from <see cref="AppSettings.Model"/>.</summary>
    private void OpenSettings()
    {
        OpenOwnForm("Settings", "Settings", AppSettings.Model, "Save", _settings.Fill, state =>
        {
            _settings.Read(state);

            if (!_settings.Save()) return _settings.LastError ?? "The settings could not be saved.";

            SetStatus("Settings saved. They apply to windows opened from now on.");
            return null;
        });
    }

    /// <summary>
    /// Empties the parameter history and the file behind it. Not asked twice: what it loses is a
    /// convenience, and keeping it was never the user's decision to begin with.
    /// </summary>
    private void ClearHistory()
    {
        _history.Forget();
        SetStatus(_history.LastError is { } error
            ? $"The history could not be cleared. {error}"
            : "Parameter history cleared.");
    }

    // ------------------------------------------------------------------ resizing a pane

    /// <summary>
    /// The keyboard route to resizing the panes (§7.14, phase0-findings 2.4): the width as a
    /// setting, in a form generated from <see cref="AppSettings.PaneModel"/>. The divider is for
    /// the mouse only.
    /// </summary>
    /// <remarks>
    /// The first version handed the divider the arrow keys, which is the other route §10 allows,
    /// and it could not work: with a <c>GridSplitter</c> focused, an arrow press never reaches
    /// WPF's key routing at all — not the splitter's handlers, not the window's, not with
    /// handled events included — while <kbd>Escape</kbd> on the same splitter does (phase6-notes).
    /// A number in a box is also simply easier to hear than a divider moving twenty pixels a press.
    /// </remarks>
    private void PaneWidth()
    {
        var current = PaneShare();
        var fitting = _settings.TreePaneWidth is null;

        OpenOwnForm("Servers pane width", "PaneWidth", AppSettings.PaneModel, "Save",
            state => AppSettings.FillPane(state, fitting, current),
            state =>
            {
                if (AppSettings.ReadPaneFit(state))
                {
                    _settings.TreePaneWidth = null;
                    _settings.Save();
                    FitPanes();
                    UpdateLayout();

                    SetStatus($"The servers pane fits what is in it: {PaneShare()} percent of the width.");
                    return null;
                }

                if (AppSettings.ReadPane(state) is not { } percent) return "Give the width as a whole number of percent, from 20 to 80.";

                var total = TreeColumn.ActualWidth + ResultColumn.ActualWidth;
                TreeColumn.Width = new GridLength(PaneLayout.FromPercent(percent, total, TreeColumn.MinWidth, ResultColumn.MinWidth));
                UpdateLayout();
                RememberPaneWidth();

                SetStatus(PaneShare() == percent
                    ? $"The servers pane is {percent} percent of the width."
                    : $"The servers pane is {PaneShare()} percent of the width, as near to {percent} as the window allows.");
                return null;
            });
    }

    private bool _fitPending;

    /// <summary>Fits the panes once the layout has caught up with whatever just changed, however many things did.</summary>
    private void FitPanesSoon()
    {
        if (_fitPending || _settings.TreePaneWidth is not null) return;

        _fitPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fitPending = false;
            FitPanes();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Makes the servers pane as wide as its widest line needs, within the limits
    /// <see cref="PaneLayout.FittedTree"/> sets — unless somebody chose a width, which is kept.
    /// </summary>
    private void FitPanes()
    {
        // A search hides the tree, and a hidden tree measures nothing: the pane would shrink to its
        // minimum under the results. It is fitted again when the tree comes back.
        if (_settings.TreePaneWidth is not null || Searching) return;

        var available = TreeColumn.ActualWidth + ResultColumn.ActualWidth;
        if (available <= 0) return;

        var fitted = PaneLayout.FittedTree(TreeContentWidth(), available, TreeColumn.MinWidth, ResultColumn.MinWidth);

        if (Math.Abs(fitted - TreeColumn.ActualWidth) >= 1) TreeColumn.Width = new GridLength(fitted);
    }

    /// <summary>
    /// How far right the tree's widest line reaches, indentation included: each item's header measured
    /// as if it had all the room it wanted, from where it sits.
    /// </summary>
    private double TreeContentWidth()
    {
        Tree.UpdateLayout();

        var widest = 0.0;

        foreach (var (_, container) in EnumerateContainers(Tree, _roots))
        {
            if (container.Template?.FindName("PART_Header", container) is not FrameworkElement header) continue;
            if (!header.IsVisible) continue;

            try
            {
                var left = header.TransformToAncestor(Tree).Transform(new Point(0, 0)).X;

                header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                widest = Math.Max(widest, left + header.DesiredSize.Width);
                header.InvalidateMeasure();
            }
            catch (InvalidOperationException)
            {
                // Not connected to the tree yet; the next pass will have it.
            }
        }

        return widest;
    }

    private int PaneShare()
    {
        var total = TreeColumn.ActualWidth + ResultColumn.ActualWidth;
        return total <= 0 ? 0 : (int)Math.Round(100 * TreeColumn.ActualWidth / total);
    }

    private void RememberPaneWidth()
    {
        if (TreeColumn.ActualWidth <= 0) return;

        _settings.TreePaneWidth = TreeColumn.ActualWidth;
        _settings.Save();
    }

    // ------------------------------------------------------------------ copying

    /// <summary>The selected server as an <c>mcpServers</c> block, ready to paste into another client.</summary>
    private void CopyServerConfiguration()
    {
        if (SelectedServer() is not { } connection)
        {
            SetStatus("Select a server in the tree first.");
            return;
        }

        Copy(ServerConfiguration.ToJson([connection.Descriptor]),
            $"Copied the configuration of {connection.Descriptor.DisplayName}, environment variables included.");
    }

    private void CopyToolSchema()
    {
        if (SelectedTool() is not { } tool)
        {
            SetStatus("Select a tool in the tree first.");
            return;
        }

        Copy(Pretty(tool.InputSchema), $"Copied the input schema of {tool.DisplayName}.");
    }

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(done);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holding the clipboard open is a reason to try again, not to crash.
            SetStatus("The clipboard is busy. Try again.");
        }
    }

    /// <summary>
    /// Puts servers just added or edited in the tree, selects the first, says what happened — and
    /// then starts them, and says how that went when it has.
    /// </summary>
    private void Adopt(IReadOnlyList<ServerConnection> connections, string message)
    {
        _removing = null;

        RebuildTree();

        if (connections.Count > 0 && _roots.FirstOrDefault(r => r.ServerId == connections[0].Descriptor.Id) is { } node)
        {
            Tree.UpdateLayout();
            if (Tree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem container)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }
        }

        UpdateRunAvailability();

        var starting = connections.Where(c => c.Descriptor.Enabled).ToList();
        SetStatus(starting.Count == 0 ? message : $"{message} Connecting.");

        if (starting.Count > 0) _ = StartAsync(starting);
    }

    private async Task StartAsync(IReadOnlyList<ServerConnection> connections)
    {
        try
        {
            await Task.WhenAll(connections.Select(c => c.ConnectAsync(_shutdown.Token)));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RefreshStale();
        UpdateRunAvailability();

        var summary = ServerAnnouncements.Startup([.. connections.Select(c => c.Snapshot())]);
        SetStatus(summary);
        SpeakIfAway(summary);
    }

    /// <summary>
    /// Rebuilds the nodes of servers that changed, and no others. Replacing one root leaves the
    /// containers of every other server alone, so a user reading one server's tools is not disturbed
    /// by another finishing its start-up — and when the caret <em>was</em> in the server that
    /// changed, it is put back on the same tool, or on the server, rather than dropped to the
    /// window (§7.25).
    /// </summary>
    private void RefreshStale()
    {
        List<ServerConnection> stale;
        lock (_stale)
        {
            stale = [.. _stale];
            _stale.Clear();
        }

        if (stale.Count == 0) return;

        // A server added or removed, or the empty-list message still showing: the shape of the
        // forest changed, so it is rebuilt whole.
        var connections = _registry.Connections;
        if (_roots.Count != connections.Count || _roots.Any(r => r.Kind != CatalogueNodeKind.Server))
        {
            RebuildTree();
            UpdateRunAvailability();
            return;
        }

        var hadFocus = Tree.IsKeyboardFocusWithin;
        var selected = Tree.SelectedItem as CatalogueNode;

        var changes = new List<string>();

        foreach (var connection in stale)
        {
            var index = IndexOf(connections, connection);
            if (index < 0) continue;

            var snapshot = connection.Snapshot();
            var replacement = ToolTreeBuilder.BuildServerNode(snapshot);
            var wasSelectedHere = selected is not null && _roots[index].SelfAndDescendants().Contains(selected);

            _roots[index] = replacement;

            if (wasSelectedHere) Reselect(replacement, selected!, hadFocus);

            if (_shown.TryGetValue(connection, out var before) && Unprompted(before, snapshot) is { Length: > 0 } change)
            {
                changes.Add(change);
                TellForms(connection, snapshot);
            }

            _shown[connection] = snapshot;
        }

        Reindex();
        UpdateRunAvailability();
        FitPanesSoon();

        // Unprompted by the user, so it is said — the tree under them just moved — but only when the
        // list really changed, and once for however many servers changed in this pass.
        if (changes.Count > 0)
        {
            AddToStatus(string.Join(" ", changes));
            SpeakIfAway(string.Join(" ", changes));
        }
    }

    /// <summary>
    /// What changed that nobody asked for: a connected server whose list changed
    /// (<c>tools/list_changed</c>), or one that stopped on its own (Phase 7). A reconnect goes through
    /// Connecting, so it is neither, and is announced by whoever asked for it instead.
    /// </summary>
    private static string Unprompted(ServerSnapshot before, ServerSnapshot after)
    {
        if (before.State != ServerConnectionState.Connected) return "";

        return after.State switch
        {
            ServerConnectionState.Connected => ServerAnnouncements.ToolsChanged(after.DisplayName, before.Tools, after.Tools),
            ServerConnectionState.Failed => ServerAnnouncements.Stopped(after),
            _ => "",
        };
    }

    /// <summary>An open form whose tool the server has stopped publishing says so, in its own status line.</summary>
    private void TellForms(ServerConnection connection, ServerSnapshot snapshot)
    {
        // A server that stopped has withdrawn nothing; its forms find out when they run.
        if (snapshot.State != ServerConnectionState.Connected) return;

        var published = snapshot.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var form in _forms.Where(f => ReferenceEquals(f.Connection, connection) && !published.Contains(f.ToolName)))
            form.Withdrawn();
    }

    private static int IndexOf(IReadOnlyList<ServerConnection> connections, ServerConnection connection)
    {
        for (var i = 0; i < connections.Count; i++)
            if (ReferenceEquals(connections[i], connection)) return i;
        return -1;
    }

    private void RebuildTree()
    {
        var hadFocus = Tree.IsKeyboardFocusWithin;
        var selected = Tree.SelectedItem as CatalogueNode;

        _roots.Clear();

        _shown.Clear();
        foreach (var connection in _registry.Connections) _shown[connection] = connection.Snapshot();

        foreach (var node in ToolTreeBuilder.BuildForest([.. _shown.Values], NoServers)) _roots.Add(node);
        FitPanesSoon();
        Reindex();

        Tree.UpdateLayout();

        if (selected?.ServerId is { } id && _roots.FirstOrDefault(r => r.ServerId == id) is { } server)
            Reselect(server, selected, hadFocus);
        else if (hadFocus)
        {
            SelectFirstNode();
            FocusTree();
        }
    }

    /// <summary>The same tool in the rebuilt node if it is still there, otherwise the server itself.</summary>
    private void Reselect(CatalogueNode server, CatalogueNode previous, bool focus)
    {
        Tree.UpdateLayout();

        var target = previous.Tool is { } tool
            ? server.SelfAndDescendants().FirstOrDefault(n => n.Tool?.Name == tool.Name)
            : server.SelfAndDescendants().FirstOrDefault(n => n.Kind == previous.Kind && n.AccessibleName == previous.AccessibleName);

        target ??= server;

        foreach (var (node, container) in EnumerateContainers(Tree, _roots))
        {
            if (!ReferenceEquals(node, target)) continue;

            container.IsSelected = true;
            container.BringIntoView();
            if (focus) Refocus(container);
            return;
        }
    }

    /// <summary>
    /// Puts the caret on a tree item that has just replaced the one it was on, and makes sure the
    /// screen reader is told.
    /// </summary>
    /// <remarks>
    /// Found by ear (phase6-notes): focusing the new item in the same pass that created it moved
    /// WPF's keyboard focus and told NVDA nothing — NVDA went on holding the item that had been
    /// removed, so a server finishing its start-up left the user on a node that no longer existed,
    /// and NVDA+Tab answered with silence. Deferred until the item is loaded and has an automation
    /// peer, and the focus event raised outright in case WPF thinks focus never moved.
    /// </remarks>
    private void Refocus(TreeViewItem container)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!container.IsLoaded) return;

            container.Focus();

            var peer = UIElementAutomationPeer.FromElement(container) ?? UIElementAutomationPeer.CreatePeerForElement(container);
            peer?.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ running a tool

    private async Task RunSelectedToolAsync()
    {
        var tool = SelectedTool();
        if (tool is null || SelectedServer() is not { } connection)
        {
            SetStatus("Select a tool in the tree first.");
            return;
        }

        // A tool with parameters gets a form window; a tool with none is a call, and making the
        // user open a window with nothing on it to press one more button would be ceremony.
        if (tool.ParameterCount > 0)
        {
            OpenForm(connection, tool);
            return;
        }

        if (_callInFlight)
        {
            SetStatus("A tool call is already running. Escape stops it.");
            return;
        }

        if (tool.Safety.IsDestructive == true && !ReferenceEquals(_confirming, tool))
        {
            _confirming = tool;
            SetStatus(ServerAnnouncements.Destructive(tool, "Enter"));
            return;
        }

        _confirming = null;
        _callInFlight = true;
        var questions = 0;
        UpdateRunAvailability();
        SetStatus($"Running {tool.DisplayName}.");

        var started = DateTimeOffset.Now;
        _currentCall = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        var throttle = new ProgressThrottle();
        var progress = new Progress<ToolProgress>(report =>
        {
            if (_callInFlight && throttle.Offer(tool.DisplayName, report) is { } line) SetStatus(line);
        });

        try
        {
            // Nothing here blocks the UI thread: the window stays responsive, which is what the
            // fixture's two-second tool is for (ROADMAP §4, pitfall 9).
            var outcome = await connection.CallAsync(tool, null, AskAsync, progress, _currentCall.Token);
            throttle.Finish();

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

                await Dispatcher.InvokeAsync(() => SpeakIfAway($"{tool.DisplayName} is asking a question."));

                return await ElicitationWindow.AskAsync(this, title, request, _windows, token);
            }

            ShowResult(tool, outcome, started);
            SetStatus(ResultFormatter.Announcement(outcome));
            SpeakIfAway($"{tool.DisplayName}: {ResultFormatter.Announcement(outcome)}");
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            SetStatus($"Stopped {tool.DisplayName}. The server was told; anything it had already done stays done.");
        }
        catch (OperationCanceledException)
        {
            // humcp is closing.
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
    private void OpenForm(ServerConnection connection, ToolInfo tool)
    {
        if (_forms.FirstOrDefault(f => ReferenceEquals(f.Connection, connection) && f.ToolName == tool.Name) is { } existing)
        {
            WindowManager.Activate(existing);
            SetStatus($"{tool.DisplayName} is already open.");
            return;
        }

        // A history that is switched off is neither offered nor added to, which MruHistory.None is.
        var form = new ToolWindow(this, tool, connection, _settings.RememberHistory ? _history : MruHistory.None,
            _windows, _settings.ShowOptionalFields, SpeakIfAway);

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

    private ToolInfo? SelectedTool() => SelectedNode()?.Tool;

    /// <summary>The server the selection is in — its own node, or anything under it.</summary>
    private ServerConnection? SelectedServer()
        => SelectedNode()?.ServerId is { } id ? _registry.Find(id) : null;

    /// <summary>What the caret's list has selected: a search's results while there is a search, the tree otherwise.</summary>
    private CatalogueNode? SelectedNode()
        => (Searching ? SearchResults.SelectedItem : Tree.SelectedItem) as CatalogueNode;

    private void SelectFirstNode()
    {
        Tree.UpdateLayout();
        if (Tree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem item) return;

        item.IsSelected = true;
        item.BringIntoView();
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

    // ------------------------------------------------------------------ tool search

    /// <summary>
    /// How long typing has to pause before the match count is said. Said at every keystroke, it
    /// would talk over the screen reader echoing the letters typed, and be out of date by the next.
    /// </summary>
    private static readonly TimeSpan SearchSettles = TimeSpan.FromMilliseconds(700);

    private ToolSearch _search = new([]);
    private System.Windows.Threading.DispatcherTimer? _searchSettled;
    private int _lastMatchCount;
    private string? _lastBestMatch;

    private bool Searching => SearchResults.Visibility == Visibility.Visible;

    /// <summary>The tools search covers changed: index them again, and search again if a search is showing.</summary>
    private void Reindex()
    {
        _search = new ToolSearch(ToolTreeBuilder.SearchEntries(_shown.Values));
        if (Searching) ShowSearchResults(keepSelection: true);
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Runs the search in the box and puts its results in the tree's place, with the caret kept in
    /// the list if it was there (§7.25).
    /// </summary>
    /// <param name="keepSelection">
    /// True when the catalogue changed under the same words — a server reconnecting — and the tool
    /// the user had selected should stay selected. False when the words changed: then the best match
    /// is selected, because it is what Enter from the box goes to. Found by ear (phase8-notes): a
    /// selection kept across new words sent Enter to a tool from the search before.
    /// </param>
    private void ShowSearchResults(bool keepSelection = false)
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            EndSearch();
            return;
        }

        var previous = keepSelection && Searching ? SearchResults.SelectedItem as CatalogueNode : null;
        var hadFocus = SearchResults.IsKeyboardFocusWithin;

        var hits = _search.Search(query);
        var nodes = ToolTreeBuilder.SearchResults(hits,
            $"No tools match {TextSanitizer.Clean(query, TextSanitizer.NameLength)}. Escape goes back to the search box.");
        _lastMatchCount = hits.Count;
        _lastBestMatch = hits.Count == 0 ? null : $"{hits[0].Entry.Tool.DisplayName}, {hits[0].Entry.ServerName}";

        SearchResults.ItemsSource = nodes;
        SearchResults.SelectedItem =
            nodes.FirstOrDefault(n => previous?.Tool is { } tool && n.Tool?.Name == tool.Name && n.ServerId == previous.ServerId)
            ?? nodes[0];

        SearchResults.Visibility = Visibility.Visible;
        Tree.Visibility = Visibility.Collapsed;

        if (hadFocus) FocusSearchResults();
        UpdateRunAvailability();
    }

    /// <summary>The tree back in its place, exactly as it was left: a search never touched it.</summary>
    private void EndSearch()
    {
        _searchSettled?.Stop();

        SearchResults.Visibility = Visibility.Collapsed;
        SearchResults.ItemsSource = null;
        Tree.Visibility = Visibility.Visible;

        UpdateRunAvailability();
        FitPanesSoon();
    }

    /// <summary>
    /// Puts the caret on the selected result. On the item, not the list: a list focused with no
    /// current item does not handle an arrow key, and the caret leaves it (§7.29).
    /// </summary>
    private bool FocusSearchResults()
    {
        if (SearchResults.SelectedItem is null && SearchResults.Items.Count > 0) SearchResults.SelectedIndex = 0;

        SearchResults.UpdateLayout();
        if (SearchResults.ItemContainerGenerator.ContainerFromItem(SearchResults.SelectedItem) is ListBoxItem item)
        {
            item.BringIntoView();
            item.Focus();
        }
        else
        {
            SearchResults.Focus();
        }

        return SearchResults.IsKeyboardFocusWithin;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ShowSearchResults();
        if (!Searching) return;

        _searchSettled ??= new System.Windows.Threading.DispatcherTimer { Interval = SearchSettles };
        _searchSettled.Stop();
        _searchSettled.Tick -= OnSearchSettled;
        _searchSettled.Tick += OnSearchSettled;
        _searchSettled.Start();
    }

    private void OnSearchSettled(object? sender, EventArgs e)
    {
        _searchSettled?.Stop();
        if (!Searching) return;

        // The best match is named, so the words can be judged without leaving the box: a count alone
        // said "41 tools match" for a search whose first result was exactly right (phase8-notes).
        SetStatus(_lastMatchCount switch
        {
            0 => "No tools match.",
            1 => $"1 tool matches: {_lastBestMatch}.",
            var n => $"{n} tools match. First: {_lastBestMatch}.",
        });
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Down or Key.Enter or Key.Return when Searching:
                e.Handled = true;
                FocusSearchResults();
                break;

            // Escape is "take me back": the search goes, and the caret goes to the tree it replaced.
            case Key.Escape:
                e.Handled = true;
                SearchBox.Clear();
                FocusTree();
                break;
        }
    }

    private void OnSearchResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                e.Handled = true;
                if (SelectedTool() is not null) _ = RunSelectedToolAsync();
                break;

            // Back to the words, to change them — the search box is above the list on screen too.
            case Key.Escape:
            case Key.Up when SearchResults.SelectedIndex <= 0:
                e.Handled = true;
                FocusSearch();
                break;
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
        // While there is a search, its results are in the tree's place, and are the pane.
        if (Searching) return FocusSearchResults();

        Tree.Focus();
        return Tree.IsKeyboardFocusWithin;
    }

    /// <summary>
    /// The one thing prism is for (§7's core rule, and ROADMAP §13's open question): text that has
    /// nowhere on screen the user can be reading it from, because no humcp window has the focus and
    /// a live region in a background window is not read. Only when the user has asked for it in
    /// Settings, because speaking from a window they are not looking at is welcome or intrusive and
    /// only they can say which. The status line still carries it, for when they come back.
    /// </summary>
    private void SpeakIfAway(string text)
    {
        if (!_settings.SpeakInBackground || string.IsNullOrWhiteSpace(text)) return;
        if (Application.Current.Windows.OfType<Window>().Any(w => w.IsActive)) return;

        _announcer.Announce($"humcp: {text}");
    }

    /// <summary>
    /// Puts a line in the status bar, which says it. This is the app's only announcement channel,
    /// and it is pure UI Automation — no prism. There is no quiet version: a reader announces a live
    /// region when its text changes, not when the event is raised (phase5-notes 4.8).
    /// </summary>
    /// <remarks>
    /// Settled by ear, which is the only way it could be settled (phase0-findings §6, checklist
    /// item 11; phase1-notes 2.7). A polite live region reaches NVDA on its own, so speaking the
    /// same string through prism as well means every completion is heard twice.
    /// </remarks>
    private void SetStatus(string text)
    {
        _statusAt = DateTime.UtcNow;
        LiveStatus.Text = text;

        var peer = UIElementAutomationPeer.FromElement(LiveStatus)
                   ?? UIElementAutomationPeer.CreatePeerForElement(LiveStatus);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ------------------------------------------------------------------ the Applications key

    /// <summary>
    /// The <kbd>Applications</kbd> key's menu (§7's keyboard map), built for the node the caret is
    /// on: what can be done to a tool, or to a server. Every item is also on the menu bar, so the
    /// context menu is a shortcut and never the only route.
    /// </summary>
    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = SelectedNode();
        var menu = ((FrameworkElement)sender).ContextMenu ??= new ContextMenu();
        menu.Items.Clear();

        if (node?.Tool is { } tool)
        {
            menu.Items.Add(Item(tool.ParameterCount > 0 ? "_Open form" : "_Run", "Enter", () => _ = RunSelectedToolAsync()));
            menu.Items.Add(Item("_Details", "F1", ShowDetails));
            menu.Items.Add(Item("Copy tool _schema", "", CopyToolSchema));
        }
        else if (node?.ServerId is not null)
        {
            menu.Items.Add(Item("_Reconnect", "F5", () => _ = ReconnectSelectedAsync()));
            menu.Items.Add(Item("_Settings…", "Alt+Enter", EditServer));
            menu.Items.Add(Item("Copy server _configuration", "", CopyServerConfiguration));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Re_move", "Delete", () => RemoveSelected()));
        }
        else
        {
            menu.Items.Add(Item("_Add server…", "Ctrl+N", AddServer));
            menu.Items.Add(Item("_Import servers…", "", ImportServers));
        }
    }

    private static MenuItem Item(string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private DateTime _statusAt;

    /// <summary>How recent a status line still is news, which something arriving unprompted must not tread on.</summary>
    private static readonly TimeSpan StatusStillNews = TimeSpan.FromSeconds(2);

    private System.Windows.Threading.DispatcherTimer? _waiting;
    private string? _waitingText;

    /// <summary>
    /// Says something nobody asked for — a server's tool list changing — once the status line has
    /// been quiet for <see cref="StatusStillNews"/>, so it never takes the place of what was just
    /// said. Found by ear (phase6-notes): a tool that changes the list finishes and notifies within
    /// milliseconds, and the change replaced the tool's own answer, so NVDA spoke only the change —
    /// phase5-notes 4.4 again. Merging the two into one line was tried next and said the answer
    /// twice. Waiting says each once, in the order they happened.
    /// </summary>
    private void AddToStatus(string text)
    {
        _waitingText = _waitingText is null ? text : $"{_waitingText} {text}";

        var quietFor = DateTime.UtcNow - _statusAt;
        if (quietFor >= StatusStillNews)
        {
            SayWaiting();
            return;
        }

        _waiting ??= new System.Windows.Threading.DispatcherTimer();
        _waiting.Stop();
        _waiting.Interval = StatusStillNews - quietFor;
        _waiting.Tick -= OnWaitingTick;
        _waiting.Tick += OnWaitingTick;
        _waiting.Start();
    }

    private void OnWaitingTick(object? sender, EventArgs e)
    {
        _waiting?.Stop();

        // Something else was said while this waited: wait again, from then.
        var quietFor = DateTime.UtcNow - _statusAt;
        if (quietFor < StatusStillNews)
        {
            _waiting!.Interval = StatusStillNews - quietFor;
            _waiting.Start();
            return;
        }

        SayWaiting();
    }

    private void SayWaiting()
    {
        if (_waitingText is not { } text) return;

        _waitingText = null;
        SetStatus(text);
    }

    // ------------------------------------------------------------------ event handlers

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => SelectionMoved();

    private void OnSearchSelectionChanged(object sender, SelectionChangedEventArgs e) => SelectionMoved();

    private void SelectionMoved()
    {
        // Deliberately silent. The tree is nouns, and UI Automation already reads the selected
        // node; speaking here would double every arrow press (§7).
        UpdateRunAvailability();

        // Moving away is changing your mind: the next Delete is a first Delete again, and the next
        // Enter on a destructive tool warns again.
        if (!ReferenceEquals(_removing, SelectedServer())) _removing = null;
        if (!ReferenceEquals(_confirming, SelectedTool())) _confirming = null;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                                && (Tree.SelectedItem as CatalogueNode)?.Kind == CatalogueNodeKind.Server)
        {
            e.Handled = true;
            RemoveSelected();
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (SelectedTool() is null) return;

        e.Handled = true;
        _ = RunSelectedToolAsync();
    }

    private void OnRun(object sender, RoutedEventArgs e) => _ = RunSelectedToolAsync();

    private void OnFind(object sender, RoutedEventArgs e) => FocusSearch();

    private void OnReconnect(object sender, RoutedEventArgs e) => _ = ReconnectSelectedAsync();

    private void OnRemoveServer(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnAddServer(object sender, RoutedEventArgs e) => AddServer();

    private void OnEditServer(object sender, RoutedEventArgs e) => EditServer();

    private void OnImportServers(object sender, RoutedEventArgs e) => ImportServers();

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnClearHistory(object sender, RoutedEventArgs e) => ClearHistory();

    private void OnPaneWidth(object sender, RoutedEventArgs e) => PaneWidth();

    private void OnCopyServer(object sender, RoutedEventArgs e) => CopyServerConfiguration();

    private void OnCopySchema(object sender, RoutedEventArgs e) => CopyToolSchema();

    /// <summary>
    /// The user guide, in the same read-only window as everything else humcp shows, so it is read the
    /// same way. Shipped beside the executable as user-guide.md.
    /// </summary>
    private void OnUserGuide(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "user-guide.md");

        string text;
        try
        {
            text = System.IO.File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            text = $"The user guide could not be read from {path}. It is also at https://github.com/fastfinge/humcp/blob/main/docs/user-guide.md";
        }

        // Read here as plain text, so the Markdown heading marks go: "number sign number sign" before
        // every heading is the file's syntax, not the guide's words.
        text = string.Join(Environment.NewLine,
            text.Split('\n').Select(line => line.TrimEnd('\r').TrimStart('#').TrimStart()));

        TextWindow.Show(this, "humcp user guide", text, _windows);
    }

    private void OnKeyboardHelp(object sender, RoutedEventArgs e)
        => TextWindow.Show(this, "Keyboard shortcuts", KeyboardHelp.Text, _windows);

    private void OnAbout(object sender, RoutedEventArgs e)
        => TextWindow.Show(this, "About humcp", string.Join(Environment.NewLine,
            "humcp",
            $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}",
            "",
            "An accessible client for Model Context Protocol servers. Every form and result is",
            "generated from what a server publishes about itself.",
            "",
            $"Servers:  {_registry.Configuration.FilePath ?? "(held in memory only)"}",
            $"Settings: {AppSettings.DefaultPath}",
            $"History:  {MruHistory.DefaultPath}"), _windows);

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

        if (SelectedNode() is not { } node)
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
        var configuration = _registry.Configuration;
        var text = new StringBuilder()
            .AppendLine($"Configuration: {configuration.FilePath ?? "(held in memory only)"}")
            .AppendLine($"Error log:     {CrashLog.Path}")
            .AppendLine(ChildProcessJob.Problem is { } job
                ? $"Server processes: not tied to humcp ({job}), so a crash can leave them running."
                : "Server processes: end when humcp ends, however it ends.")
            .AppendLine($"Servers:       {_registry.Connections.Count}");

        if (configuration.LastError is { } error) text.AppendLine($"Problem:       {error}");

        if (configuration.Problems.Count > 0)
        {
            text.AppendLine().AppendLine("Entries that could not be read:");
            foreach (var problem in configuration.Problems) text.AppendLine("  " + problem);
        }

        // The selected server first, because that is almost always the one being asked about.
        var selected = SelectedServer();

        foreach (var connection in _registry.Connections.OrderByDescending(c => ReferenceEquals(c, selected)))
        {
            var snapshot = connection.Snapshot();
            var diagnostics = connection.Diagnostics;
            var environment = connection.Descriptor.Environment;

            text.AppendLine()
                .AppendLine(snapshot.DisplayName)
                .AppendLine(new string('-', 60))
                .AppendLine($"Key:         {connection.Descriptor.Id}")
                .AppendLine($"Command:     {connection.Descriptor.CommandLine}")
                .AppendLine($"Directory:   {connection.Descriptor.WorkingDirectory ?? "(humcp's own)"}")

                // Names only. The values are where API keys live, and a diagnostics window is a
                // thing people copy into bug reports.
                .AppendLine($"Environment: {(environment.Count == 0 ? "(none set)" : string.Join(", ", environment.Keys) + " (values not shown)")}")
                .AppendLine($"Enabled:     {(connection.Descriptor.Enabled ? "yes" : "no")}")
                .AppendLine($"State:       {snapshot.State}")
                .AppendLine($"Protocol:    {snapshot.NegotiatedProtocolVersion ?? "(not negotiated)"}")
                .AppendLine($"Server said its name is: {connection.ServerTitle ?? "(nothing yet)"}")
                .AppendLine($"Tools:       {snapshot.Tools.Count}")
                .AppendLine($"Failure:     {snapshot.FailureReason ?? "(none)"}")
                .AppendLine()
                .AppendLine("Standard error from the server process:")
                .AppendLine(diagnostics.Count == 0 ? "  (nothing)" : string.Join(Environment.NewLine, diagnostics));
        }

        TextWindow.Show(this, "Connection diagnostics", text.ToString(), _windows);
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

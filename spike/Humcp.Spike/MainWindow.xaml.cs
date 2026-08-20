using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Humcp.Spike;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand SubmitCommand = new();
    public static readonly RoutedCommand NextPaneCommand = new();
    public static readonly RoutedCommand PreviousPaneCommand = new();
    public static readonly RoutedCommand DetailsCommand = new();
    public static readonly RoutedCommand FocusFilterCommand = new();

    private readonly ObservableCollection<SpikeField> _fields = new();
    private readonly ObservableCollection<ValidationError> _errors = new();
    private readonly IAnnouncer _announcer;
    private readonly List<string> _log = new();

    private List<SpikeField> _allFields = new();
    private int _paneIndex;
    private int _addedFieldCount;
    private string _lastErrorSignature = "";

    public MainWindow()
    {
        InitializeComponent();

        _announcer = PrismAnnouncer.Create(line =>
        {
            lock (_log) _log.Add(line);
        });
        if (_announcer is PrismAnnouncer prism)
            prism.AvailabilityChanged += msg => Dispatcher.Invoke(() => SetStatus(msg, speak: true));

        CommandBindings.Add(new CommandBinding(SubmitCommand, (_, _) => Submit()));
        CommandBindings.Add(new CommandBinding(NextPaneCommand, (_, _) => CyclePane(+1)));
        CommandBindings.Add(new CommandBinding(PreviousPaneCommand, (_, _) => CyclePane(-1)));
        CommandBindings.Add(new CommandBinding(DetailsCommand, (_, _) => ShowDetailsForFocus()));
        CommandBindings.Add(new CommandBinding(FocusFilterCommand, (_, _) => FilterBox.Focus()));

        Tree.ItemsSource = SpikeData.BuildTree();
        FormItems.ItemsSource = _fields;
        ErrorList.ItemsSource = _errors;

        RebuildForm();

        Loaded += (_, _) =>
        {
            Tree.Focus();
            SetStatus(_announcer.IsAvailable
                ? "humcp Phase 0 spike ready. Speech available."
                : "humcp Phase 0 spike ready. Speech unavailable; the app is running on the no-op announcer.",
                speak: true);
        };

        Closed += (_, _) => _announcer.Dispose();
    }

    // ------------------------------------------------------------------ form construction

    private void RebuildForm()
    {
        var hideOptional = HideOptionalItem.IsChecked;

        _allFields = SpikeData.BuildFields(_addedFieldCount);
        var visible = _allFields.Where(f => !hideOptional || f.IsRequired || f.Kind == FieldKind.Group).ToList();

        AssignAccessKeys(visible);

        _fields.Clear();
        foreach (var f in visible) _fields.Add(f);

        _errors.Clear();
        ErrorPanel.Visibility = Visibility.Collapsed;
        _lastErrorSignature = "";

        FormHeading.Text = $"Parameters — list_pull_requests ({CountLeafFields()} fields)";
    }

    private int CountLeafFields()
        => _fields.SelectMany(f => f.SelfAndDescendants()).Count(f => f.Kind != FieldKind.Group);

    /// <summary>
    /// §7.3 wants a real label with an access key. Access keys have to be allocated at runtime
    /// because the field set is generated — this is the allocator, and duplicates are the risk
    /// being tested (WPF cycles between duplicate access keys instead of activating).
    /// </summary>
    private static void AssignAccessKeys(IEnumerable<SpikeField> fields)
    {
        var used = new HashSet<char>();

        // Reserve the access keys already spent on the chrome: File, View, Speech, Run, Add,
        // fields, Reset, Filter.
        foreach (var c in "fvsraril") used.Add(char.ToLowerInvariant(c));

        foreach (var field in fields.SelectMany(f => f.SelfAndDescendants()))
        {
            var label = field.Label;
            var index = -1;
            for (var i = 0; i < label.Length; i++)
            {
                if (!char.IsLetterOrDigit(label[i])) continue;
                if (used.Add(char.ToLowerInvariant(label[i]))) { index = i; break; }
            }

            field.LabelWithAccessKey = index >= 0
                ? label.Insert(index, "_")
                : label.Replace("_", "__"); // no key left; escape any literal underscores
        }
    }

    // ------------------------------------------------------------------ validation and submit

    private void Submit()
    {
        foreach (var f in _fields.SelectMany(x => x.SelfAndDescendants()))
            f.ItemStatus = "";

        var errors = Validate().ToList();
        var signature = string.Join("|", errors.Select(e => e.Display));

        if (errors.Count > 0 && signature != _lastErrorSignature)
        {
            // Warn, list, focus the first offender — but do not refuse. §5: the server is the
            // authority, and an over-strict client makes a working tool unusable. A second
            // Run on the same errors submits anyway.
            _lastErrorSignature = signature;

            _errors.Clear();
            foreach (var e in errors)
            {
                e.Field.ItemStatus = "Invalid. " + e.Message;
                _errors.Add(e);
            }

            ErrorHeading.Text = errors.Count == 1 ? "1 validation error" : $"{errors.Count} validation errors";
            ErrorPanel.Visibility = Visibility.Visible;
            UpdateLayout();

            var first = errors[0];
            var focused = FocusField(first.Field);

            var message = $"{errors.Count} validation error{(errors.Count == 1 ? "" : "s")}. " +
                          (focused
                              ? $"Focus moved to {first.Field.Label}. Run again to submit anyway."
                              : $"First error: {first.Display}. Run again to submit anyway.");
            SetStatus(message, speak: true);
            return;
        }

        _errors.Clear();
        ErrorPanel.Visibility = Visibility.Collapsed;
        _lastErrorSignature = "";

        ResultBox.Text = SpikeData.RenderResult(_fields);
        var lines = ResultBox.Text.Split('\n').Length;
        SetStatus($"Tool call complete. {lines} lines of result.", speak: true);
    }

    private IEnumerable<ValidationError> Validate()
    {
        foreach (var f in _fields.SelectMany(x => x.SelfAndDescendants()))
        {
            switch (f.Kind)
            {
                case FieldKind.Group:
                    continue;

                case FieldKind.Bool:
                    continue;

                case FieldKind.Number:
                {
                    var raw = f.Value as string;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (f.IsRequired) yield return new ValidationError(f, "is required.");
                        continue;
                    }
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        yield return new ValidationError(f, "must be a whole number.");
                    continue;
                }

                case FieldKind.DateAsText:
                {
                    var raw = f.Value as string;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (f.IsRequired) yield return new ValidationError(f, "is required.");
                        continue;
                    }
                    if (!DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out _))
                        yield return new ValidationError(f, "must be a date in the form 2026-08-20.");
                    continue;
                }

                case FieldKind.Date:
                {
                    if (f.IsRequired && f.DateValue is null)
                        yield return new ValidationError(f, "is required.");
                    continue;
                }

                default:
                {
                    if (f.IsRequired && string.IsNullOrWhiteSpace(f.Value as string))
                        yield return new ValidationError(f, "is required.");
                    continue;
                }
            }
        }
    }

    // ------------------------------------------------------------------ focus plumbing

    private bool FocusField(SpikeField field)
    {
        FormItems.UpdateLayout();
        var control = FindInput(FormItems, field);
        if (control is null) return false;

        control.BringIntoView();
        return control.Focus();
    }

    private static Control? FindInput(DependencyObject root, SpikeField field)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Control c && c.Tag as string == "input" && ReferenceEquals(c.DataContext, field))
                return c;
            var found = FindInput(child, field);
            if (found is not null) return found;
        }
        return null;
    }

    private void CyclePane(int direction)
    {
        var panes = new (string Name, Action Focus)[]
        {
            ("Servers tree", () => Tree.Focus()),
            ("Parameters", () => { if (!FocusFirstInput()) SubmitButton.Focus(); }),
            ("Result", () => ResultBox.Focus()),
        };

        _paneIndex = (_paneIndex + direction + panes.Length) % panes.Length;
        panes[_paneIndex].Focus();

        // No UIA event says "the pane changed", so this is prism's job (§7: verbs).
        _announcer.Announce(panes[_paneIndex].Name, interrupt: true);
    }

    private bool FocusFirstInput()
    {
        FormItems.UpdateLayout();
        var first = _fields.SelectMany(f => f.SelfAndDescendants()).FirstOrDefault(f => f.Kind != FieldKind.Group);
        return first is not null && FocusField(first);
    }

    // ------------------------------------------------------------------ announcements

    /// <summary>
    /// Sets the status text (a polite live region) and optionally speaks it. Both paths exist so
    /// the manual test can tell whether WPF live regions alone are enough, or whether prism has
    /// to carry every completion announcement.
    /// </summary>
    private void SetStatus(string text, bool speak)
    {
        LiveStatus.Text = text;

        var peer = UIElementAutomationPeer.FromElement(LiveStatus)
                   ?? UIElementAutomationPeer.CreatePeerForElement(LiveStatus);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

        if (speak) _announcer.Announce(text);
    }

    // ------------------------------------------------------------------ event handlers

    private void OnSubmit(object sender, RoutedEventArgs e) => Submit();

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        _addedFieldCount++;
        RebuildForm();
        var total = CountLeafFields();
        SetStatus($"Field added. {total} fields on the form.", speak: true);
    }

    private void OnAddManyFields(object sender, RoutedEventArgs e)
    {
        _addedFieldCount += 200;
        RebuildForm();
        SetStatus($"200 fields added. {CountLeafFields()} fields on the form.", speak: true);
    }

    private void OnResetForm(object sender, RoutedEventArgs e)
    {
        _addedFieldCount = 0;
        RebuildForm();
        ResultBox.Text = "No tool has been run yet.";
        SetStatus("Form reset.", speak: true);
    }

    private void OnRebuildForm(object sender, RoutedEventArgs e)
    {
        RebuildForm();
        SetStatus(HideOptionalItem.IsChecked
            ? $"Optional fields hidden. {CountLeafFields()} fields on the form."
            : $"All fields shown. {CountLeafFields()} fields on the form.", speak: true);
    }

    private void OnToggleVirtualization(object sender, RoutedEventArgs e)
    {
        // The §7.6 experiment: flip virtualization on and count what remains in the UIA tree.
        VirtualizingStackPanel.SetIsVirtualizing(FormItems, VirtualizeItem.IsChecked);
        FormItems.Items.Refresh();
        FormItems.UpdateLayout();
        SetStatus(VirtualizeItem.IsChecked
            ? "Form virtualization ON. Off-screen fields are expected to vanish from the accessibility tree."
            : "Form virtualization OFF.", speak: true);
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Deliberately silent: the tree is nouns, and UIA already reads the selected node.
        // Speaking here would make every arrow-press double-speak (§7).
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        var roots = SpikeData.BuildTree();
        if (text.Length > 0) roots = SpikeData.Filter(roots, text);
        Tree.ItemsSource = roots;

        var matches = roots.SelectMany(SpikeData.Flatten).Count(n => n.Children.Count == 0);
        if (text.Length > 0) SetStatus($"{matches} tools match “{text}”.", speak: false);
    }

    private void OnErrorListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ActivateSelectedError();
    }

    private void OnErrorListActivated(object sender, MouseButtonEventArgs e) => ActivateSelectedError();

    private void ActivateSelectedError()
    {
        if (ErrorList.SelectedItem is ValidationError error) FocusField(error.Field);
    }

    private void ShowDetailsForFocus()
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        if (focused?.DataContext is SpikeField field)
        {
            ShowTextWindow($"Details: {field.Label}",
                $"Name: {field.Name}\r\nRequired: {field.IsRequired}\r\n\r\n{field.FullDescription}");
            return;
        }

        if (Tree.SelectedItem is TreeNode node)
        {
            ShowTextWindow($"Details: {node.Label}", node.AccessibleName);
            return;
        }

        SetStatus("No details for the focused item.", speak: true);
    }

    private void OnShowDiagnostics(object sender, RoutedEventArgs e)
    {
        string log;
        lock (_log) log = string.Join(Environment.NewLine, _log.TakeLast(200));

        var text = new StringBuilder()
            .AppendLine(_announcer.Diagnostics)
            .AppendLine()
            .AppendLine("prism log:")
            .AppendLine(log.Length == 0 ? "  (no messages)" : log)
            .ToString();

        ShowTextWindow("prism diagnostics", text);
    }

    private void OnSpeakTest(object sender, RoutedEventArgs e)
    {
        _announcer.Announce("Speech only. Tool call complete, 3 results.", interrupt: true);
        SetStatus("Sent a speech-only test phrase.", speak: false);
    }

    private void OnBrailleTest(object sender, RoutedEventArgs e)
    {
        _announcer.Braille("Braille only. 3 results.");
        SetStatus("Sent a braille-only test phrase.", speak: false);
    }

    private void OnOutputTest(object sender, RoutedEventArgs e)
    {
        _announcer.Output("Speech and braille together. Connection to github lost.", interrupt: true);
        SetStatus("Sent a combined speech and braille test phrase.", speak: false);
    }

    private void OnStopSpeech(object sender, RoutedEventArgs e) => _announcer.Stop();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ helpers

    /// <summary>A read-only multiline TextBox in a window — §7.9's rule applied to dialogs too.</summary>
    private void ShowTextWindow(string title, string body)
    {
        var box = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(8),
        };
        AutomationProperties.SetName(box, title);

        var window = new Window
        {
            Title = title,
            Owner = this,
            Width = 720,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = box,
        };
        window.InputBindings.Add(new KeyBinding(ApplicationCommands.Close, Key.Escape, ModifierKeys.None));
        window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => window.Close()));
        window.Loaded += (_, _) => box.Focus();
        window.Show(); // non-modal, per §8
    }
}

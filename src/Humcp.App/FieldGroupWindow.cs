using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// One row of a list of objects, in a window of its own (ROADMAP §5, Tier 2) — or, since 1.1, one
/// entry of a list of values, opened to be changed in place.
/// </summary>
/// <remarks>
/// <para>
/// A window rather than a third level of nesting on the parent form. A list of objects is the only
/// shape in the schema engine that can put a whole form inside a row of a list inside a form, and
/// arrowing through that by ear means never being sure which of the three things has focus. A
/// window has a name that is announced on entry and a boundary that is obvious from either side.
/// </para>
/// <para>
/// Edits are live: what is typed here is in the row as it is typed, and closing keeps it. There is
/// no Cancel, because a Cancel would need a copy of the row to roll back to, and "your changes were
/// discarded" is a state a user working by ear finds out about too late to do anything with.
/// Removing an unwanted row is one keystroke on the list it came from.
/// </para>
/// </remarks>
internal static class FieldGroupWindow
{
    public static void Show(Window owner, string title, GroupFieldState row, WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(row);
        Show(owner, title, row.Fields, windows);
    }

    /// <param name="closeOnEnter">
    /// For a single entry: Enter closes the window, as it would a dialog asking for one value. A
    /// row of several fields keeps Enter for its fields.
    /// </param>
    public static void Show(
        Window owner, string title, IReadOnlyList<FieldState> members, WindowManager windows, bool closeOnEnter = false)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(windows);

        AssignAccessKeys(members);

        var fields = new FormItemsControl
        {
            ItemsSource = members,
            ItemTemplateSelector = new FieldTemplateSelector(),
            Margin = new Thickness(10, 6, 10, 10),
        };

        AutomationProperties.SetName(fields, title);

        // Focusable is false for the same reason it is on the form itself: a scroll viewer is not a
        // control, and WPF makes one a tab stop the moment it has something to scroll (§7.14).
        var scroller = new ScrollViewer
        {
            Content = fields,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var heading = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Margin = new Thickness(10, 10, 10, 0),
        };

        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);

        // Command, not IsCancel: IsCancel closes a modal dialog and does nothing at all on a window
        // shown with Show() (phase3-notes 4.2).
        var close = new Button
        {
            Content = "_Close",
            Command = WindowManager.CloseWindow,
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(10, 0, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        AutomationProperties.SetHelpText(close, closeOnEnter
            ? "Closes this entry. Enter and Escape do the same. What you typed is kept."
            : "Closes this entry. Escape does the same. What you typed is kept.");

        // A live region of its own, because the commands below have things to say — "added to
        // Tags, two entries now" — and until Phase 5 this window had neither the commands nor
        // anywhere to say what they did (phase5-notes 4.9).
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(status, "RowStatus");

        var statusBar = new StatusBar { Focusable = false, IsTabStop = false };
        statusBar.Items.Add(new StatusBarItem { Content = status });

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(statusBar);
        layout.Children.Add(close);
        layout.Children.Add(scroller);

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 620,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout,
        };

        // §7.17: without this the window announces its entire content as its name.
        AutomationProperties.SetName(window, title);

        windows.Track(window);

        // A routed command bubbles up the tree it was raised in, and this is a window of its own —
        // so the owner's bindings never see it. A list inside a row of an object list had a dead
        // Add button for exactly as long as nobody put a list inside one (phase5-notes 4.9).
        FormCommandBindings.Attach(window, members, text => Say(status, text), windows);

        if (closeOnEnter)
            window.InputBindings.Add(new KeyBinding(WindowManager.CloseWindow, Key.Enter, ModifierKeys.None));

        var bar = windows.CreateMenuBar(window, closable: true);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Insert(0, bar);

        window.Loaded += (_, _) => Focus(fields, caretToEnd: closeOnEnter);
        window.Show();
    }

    /// <summary>
    /// Access keys for this window's own fields. The parent form assigned its own when it opened,
    /// and these fields did not exist then — a row is made after the fact, and its window is a
    /// separate place for <kbd>Alt</kbd> to mean something.
    /// </summary>
    private static void AssignAccessKeys(IReadOnlyList<FieldState> members)
    {
        var fields = members.SelectMany(FormState.Labelled).ToList();
        var indices = AccessKeys.Assign([.. fields.Select(f => f.Label)], ['c']);

        for (var i = 0; i < fields.Count; i++) fields[i].AccessKeyIndex = indices[i];
    }

    private static void Say(TextBlock status, string text)
    {
        LiveRegion.Say(status, text);
    }

    private static void Focus(ItemsControl fields, bool caretToEnd)
    {
        fields.UpdateLayout();
        fields.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

        // An entry opened to be changed is usually changed at its end — the value after NAME= —
        // and a caret left at the start puts the whole entry between the user and it.
        if (caretToEnd && Keyboard.FocusedElement is TextBox box) box.CaretIndex = box.Text.Length;
    }
}

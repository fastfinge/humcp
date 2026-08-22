using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// One row of a list of objects, in a window of its own (ROADMAP §5, Tier 2).
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
        ArgumentNullException.ThrowIfNull(windows);

        AssignAccessKeys(row);

        var fields = new FormItemsControl
        {
            ItemsSource = row.Fields,
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

        AutomationProperties.SetHelpText(close,
            "Closes this entry. Escape does the same. What you typed is kept.");

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(heading);
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

        var bar = windows.CreateMenuBar(window, closable: true);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Insert(0, bar);

        window.Loaded += (_, _) => Focus(fields);
        window.Show();
    }

    /// <summary>
    /// Access keys for this window's own fields. The parent form assigned its own when it opened,
    /// and these fields did not exist then — a row is made after the fact, and its window is a
    /// separate place for <kbd>Alt</kbd> to mean something.
    /// </summary>
    private static void AssignAccessKeys(GroupFieldState row)
    {
        var fields = row.Fields.SelectMany(FormState.Labelled).ToList();
        var indices = AccessKeys.Assign([.. fields.Select(f => f.Label)], ['c']);

        for (var i = 0; i < fields.Count; i++) fields[i].AccessKeyIndex = indices[i];
    }

    private static void Focus(ItemsControl fields)
    {
        fields.UpdateLayout();
        fields.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}

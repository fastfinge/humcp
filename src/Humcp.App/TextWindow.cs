using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Humcp.App;

/// <summary>
/// A non-modal window holding one read-only, caret-navigable text box — §7.9's rule applied to
/// dialogs as well as to results, and §8's "windows are non-modal" applied from the first phase.
/// </summary>
internal static class TextWindow
{
    public static void Show(Window owner, string title, string body, WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

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
        AutomationProperties.SetHelpText(box, "Read-only. Escape closes this window.");

        var layout = new DockPanel();
        layout.Children.Add(box);

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout,
        };

        // Without this, the window's accessible name is the plain text of its content — a WPF
        // Window is a ContentControl, and its peer falls back to the content when no name is set.
        // A screen reader would announce the entire body as the window title.
        AutomationProperties.SetName(window, title);

        // Tracked like every other window (§8): a menu bar of its own, a place in everybody else's
        // Window menu, out of Alt+Tab, and Escape closes it.
        windows.Track(window);

        var bar = windows.CreateMenuBar(window, closable: true);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Insert(0, bar);

        window.Loaded += (_, _) => box.Focus();
        window.Show();
    }
}

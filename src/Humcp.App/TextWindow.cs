using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Humcp.App;

/// <summary>
/// A non-modal window holding one read-only, caret-navigable text box — §7.9's rule applied to
/// dialogs as well as to results, and §8's "windows are non-modal" applied from the first phase.
/// </summary>
internal static class TextWindow
{
    public static void Show(Window owner, string title, string body)
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
        AutomationProperties.SetHelpText(box, "Read-only. Escape closes this window.");

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = box,
        };

        // Without this, the window's accessible name is the plain text of its content — a WPF
        // Window is a ContentControl, and its peer falls back to the content when no name is set.
        // A screen reader would announce the entire body as the window title.
        AutomationProperties.SetName(window, title);

        window.InputBindings.Add(new KeyBinding(ApplicationCommands.Close, Key.Escape, ModifierKeys.None));
        window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => window.Close()));
        window.Loaded += (_, _) => box.Focus();

        window.Show();
    }
}

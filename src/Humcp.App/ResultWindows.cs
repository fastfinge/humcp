using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// Opening one piece of a result in a window of its own — what <kbd>Enter</kbd> does wherever the
/// caret is in a result (ROADMAP §10, Phase 4).
/// </summary>
/// <remarks>
/// Which window depends on the shape and on nothing else, which is the whole of the rule: a table
/// is a grid, text long enough to want a caret is a text window, and an object or a list is a tree.
/// A short single value opens nothing, because a window holding the word "22.5" is a window to
/// close rather than a window to read.
/// </remarks>
internal static class ResultWindows
{
    /// <summary>Opens <paramref name="node"/>, or answers with what to say instead.</summary>
    public static string? Open(Window owner, ResultNode node, WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(node);

        var title = node.Label.Length > 0 ? node.Label : "Result";

        switch (node.Shape)
        {
            case ResultShape.Table:
                GridWindow.Show(owner, $"{title} — {node.Summary}", node, windows);
                return null;

            case ResultShape.Value when node.IsLongText:
                TextWindow.Show(owner, title, node.Text, windows);
                return null;

            case ResultShape.Value:
                // Already on screen and already spoken; opening a window would be ceremony.
                return $"{node.AccessibleName}. Control plus Shift plus C copies it.";

            default:
                ShowTree(owner, $"{title} — {node.Summary}", node, windows);
                return null;
        }
    }

    /// <summary>
    /// A nested object or list, as the same tree the result pane uses.
    /// </summary>
    /// <remarks>
    /// It reads identically because it is built from the same two resources
    /// (<c>Result.NodeStyle</c> and <c>Result.NodeTemplate</c>) rather than from a second copy of
    /// them. The first Phase 4 pass met this case as raw JSON — honest, and a step down from the
    /// tree the same value had a moment earlier.
    /// </remarks>
    private static void ShowTree(Window owner, string title, ResultNode node, WindowManager windows)
    {
        var status = new TextBlock { Margin = new Thickness(4, 2, 4, 2), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var tree = new TypeAheadTreeView
        {
            ItemsSource = new[] { node },
            Margin = new Thickness(8),
            ItemContainerStyle = Resource<Style>("Result.NodeStyle"),
            ItemTemplate = Resource<HierarchicalDataTemplate>("Result.NodeTemplate"),
        };

        AutomationProperties.SetName(tree, node.AccessibleName);
        AutomationProperties.SetHelpText(tree,
            "Arrow keys open and close the branches. Enter opens whatever the caret is on in a " +
            "window of its own. Control plus Shift plus C copies it.");

        var layout = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);
        layout.Children.Add(tree);

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

        // The same two keystrokes as the tree it came from: a window opened from a tree that then
        // behaved differently would be a mode nobody announced.
        window.CommandBindings.Add(new CommandBinding(ResultCommands.Expand, (_, _) =>
        {
            if (tree.SelectedItem is ResultNode selected && Open(window, selected, windows) is { } message)
                Say(status, message);
        }));

        window.CommandBindings.Add(new CommandBinding(ResultCommands.Copy, (_, _) =>
        {
            if (tree.SelectedItem is not ResultNode selected) return;

            Say(status, Launcher.Copy(selected.Label,
                selected.Shape == ResultShape.Value ? selected.Text : selected.Json()));
        }));

        tree.InputBindings.Add(new KeyBinding(ResultCommands.Expand, new KeyGesture(Key.Enter)));
        tree.InputBindings.Add(new KeyBinding(ResultCommands.Copy,
            new KeyGesture(Key.C, ModifierKeys.Control | ModifierKeys.Shift)));

        // The node this window is about is open when it opens: it is not the root of the result, so
        // the "expand the root" binding in Result.NodeStyle does not fire for it, and a window that
        // opens on one collapsed line is a window that costs a keystroke to read (§7.29's cousin).
        window.Loaded += (_, _) =>
        {
            tree.UpdateLayout();

            if (tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem root)
            {
                root.IsExpanded = true;
                root.IsSelected = true;
            }

            tree.Focus();
        };

        window.Show();
    }

    private static void Say(TextBlock status, string message)
    {
        LiveRegion.Say(status, message);
    }

    private static T Resource<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T
        ?? throw new InvalidOperationException($"The application resources have no {key}.");
}

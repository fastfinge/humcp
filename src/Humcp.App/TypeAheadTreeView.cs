using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// A <see cref="TreeView"/> with the first-letter navigation Windows assumes every list-shaped
/// control has, and WPF's does not (ROADMAP §7.26).
/// </summary>
/// <remarks>
/// <para>
/// Typing a letter moves to the next node beginning with it; typing several in quick succession
/// matches the whole run; pressing the same letter repeatedly cycles the nodes beginning with it.
/// That is what a list box, a combo box and Explorer all do — it is <c>TextSearch</c> — and a
/// <c>TreeView</c> simply drops the keystroke, which a Phase 3 manual pass found by pressing S on a
/// nine-tool server and going nowhere (phase3-notes 4.10).
/// </para>
/// <para>
/// Written as a control rather than as a handler on one window because §7.26 is a rule about every
/// control of a shape, and Phase 4 added a second tree. A rule kept in two places is a rule that is
/// true in one of them.
/// </para>
/// <para>
/// It matches on each node's <em>accessible</em> name, which is what the user heard. Where an item
/// has none it falls back to the header's text, so a tree that has not set one still navigates.
/// </para>
/// </remarks>
public class TypeAheadTreeView : TreeView
{
    /// <summary>How long a typed run stays one search, matching the shell's own type-ahead.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private string _prefix = "";
    private DateTime _prefixAt;

    public TypeAheadTreeView()
    {
        // §7.6 from the other side: a virtualized node is not in the UI Automation tree at all, so
        // a tree that scrolls is one whose contents a screen reader cannot count. Set here rather
        // than at each use, because a rule that has to be remembered in two XAML files is a rule
        // that will be forgotten in one of them.
        VirtualizingPanel.SetIsVirtualizing(this, false);
    }

    /// <summary>
    /// Selects the first node when the tree takes focus with nothing selected (§7.29).
    /// </summary>
    /// <remarks>
    /// The same rule a list box needs (phase3-notes 4.8) and a grid needs (phase4-notes 4.3): a
    /// control that moves with the arrow keys has to have somewhere for them to move from. Without
    /// it the first press is spent arriving rather than moving, and every command that acts on "the
    /// selected node" is unavailable until then.
    /// </remarks>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (SelectedItem is not null || Items.Count == 0) return;

        UpdateLayout();
        if (ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem first) return;

        // Selected *and* focused, in that order. Selecting alone leaves the caret on the tree
        // itself, and the first arrow press then moves focus to the second node while the selection
        // is still catching up on the first — which a reader announces as "not selected", heard in
        // the Phase 4 re-check.
        first.IsSelected = true;
        first.Focus();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        ArgumentNullException.ThrowIfNull(e);

        if (e.Handled) return;

        var typed = e.Text;
        if (typed.Length == 0 || char.IsControl(typed[0])) return;

        var now = DateTime.UtcNow;
        _prefix = now - _prefixAt > Window ? typed : _prefix + typed;
        _prefixAt = now;

        // A repeated single letter means "the next one starting with it" rather than a literal run
        // of that letter, which is how every other list on Windows behaves.
        var target = _prefix.Length > 1 && _prefix.All(c => c == _prefix[0]) ? _prefix[..1] : _prefix;

        if (Select(target)) e.Handled = true;
    }

    private bool Select(string prefix)
    {
        var containers = Containers(this).ToList();
        if (containers.Count == 0) return false;

        // From just after the current selection, wrapping, so repeated presses cycle the matches.
        var start = containers.FindIndex(c => c.IsSelected) + 1;

        for (var i = 0; i < containers.Count; i++)
        {
            var container = containers[(start + i) % containers.Count];

            if (!NameOf(container).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) continue;

            container.IsSelected = true;
            container.BringIntoView();
            container.Focus();
            return true;
        }

        return false;
    }

    private static string NameOf(TreeViewItem container)
    {
        var name = AutomationProperties.GetName(container);
        return name.Length > 0 ? name : container.Header?.ToString() ?? "";
    }

    /// <summary>Every realised item, in the order they are read, expanded ones included.</summary>
    private static IEnumerable<TreeViewItem> Containers(ItemsControl parent)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem container) continue;

            yield return container;

            if (!container.IsExpanded) continue;

            container.UpdateLayout();
            foreach (var descendant in Containers(container)) yield return descendant;
        }
    }
}

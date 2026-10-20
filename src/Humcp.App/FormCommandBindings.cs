using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// The command bindings every window that hosts a generated form needs, in one place.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DataTemplate"/> has no code behind it, so a button inside one asks by command and
/// the window answers (see <see cref="FormCommands"/>). Which means <b>a window that hosts the
/// templates and does not answer their commands has a form whose buttons are permanently
/// disabled</b> — and a disabled button is not in the tab order, so it is not merely inert but
/// unreachable, while its own help text goes on telling the user to press it.
/// </para>
/// <para>
/// That is exactly what happened to the first question window: it bound Accept and Decline and
/// nothing else, so a multi-select enumeration — which the elicitation subset has two of — could be
/// chosen from and never added to (phase5-notes 4.9). Nothing announced it; the button was simply
/// not there when Tab went past. The sub-form window an object list opens had never had them at all,
/// for a different reason with the same effect: a routed command bubbles up the tree it was raised
/// in, and that is a window of its own, so the owner's bindings never see it.
/// </para>
/// <para>
/// Shared here rather than copied, because a rule that has to be remembered in three files is a
/// rule that will be forgotten in two of them — which is <see cref="FormItemsControl"/>'s own
/// comment about virtualization, proved again one layer up.
/// </para>
/// </remarks>
internal static class FormCommandBindings
{
    /// <summary>
    /// Binds the list and sub-form commands on <paramref name="window"/>.
    /// </summary>
    /// <param name="fields">
    /// The fields on this window, so that a list can tell the command system when its Add button
    /// has something to add. See <see cref="KeepAddButtonsCurrent"/>.
    /// </param>
    /// <param name="announce">Says what happened, since none of these has any other voice.</param>
    /// <param name="windows">Where a sub-form window is tracked.</param>
    public static void Attach(
        Window window, IEnumerable<FieldState> fields, Action<string> announce, WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(announce);
        ArgumentNullException.ThrowIfNull(windows);

        KeepAddButtonsCurrent(fields);

        Add(FormCommands.AddEntry, OnAddEntry, (_, e) => e.CanExecute = Entry(e) is ListFieldState { CanAdd: true });
        Add(FormCommands.RemoveEntry, OnRemoveEntry, (_, e) => e.CanExecute = Selected(e) is not null);
        Add(FormCommands.EditEntry, OnEditEntry, (_, e) => e.CanExecute = Selected(e) is FieldState);
        Add(FormCommands.AddRow, OnAddRow);
        Add(FormCommands.EditRow, OnEditRow, (_, e) => e.CanExecute = Selected(e) is GroupFieldState);

        void Add(RoutedUICommand command, ExecutedRoutedEventHandler executed,
            CanExecuteRoutedEventHandler? canExecute = null)
            => window.CommandBindings.Add(new CommandBinding(command, executed, canExecute));

        void OnAddEntry(object sender, ExecutedRoutedEventArgs e)
        {
            var list = e.Parameter switch
            {
                ListFieldState direct => direct,
                ListBox box => box.DataContext as ListFieldState,
                _ => null,
            };

            if (list is null || !list.AddDraft()) return;

            // Said out loud because nothing else would: the entry box empties and the list grows,
            // and neither of those is an event a screen reader announces on its own.
            announce($"Added to {list.Label}. {Count(list.Count)} now.");

            // And the caret goes back to the box. Adding empties the draft, which makes CanAdd
            // false, which disables the very button being pressed — and WPF drops focus from a
            // disabled control to the window, where a screen reader says the window's title and
            // stops (§7.25, phase5-notes 4.10). The box is where the next value would be typed
            // anyway, so this is the useful place as well as the safe one.
            ReturnToEntryBox(e.OriginalSource);
        }

        void OnRemoveEntry(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ListBox box) return;

            switch (box.DataContext, box.SelectedItem)
            {
                case (ListFieldState list, FieldState entry):
                    var index = box.SelectedIndex;
                    list.Remove(entry);
                    announce($"Removed from {list.Label}. {Count(list.Count)} left.");
                    Reselect(box, index);
                    break;

                case (ObjectListFieldState rows, GroupFieldState row):
                    var position = box.SelectedIndex;
                    rows.Remove(row);
                    announce($"Removed from {rows.Label}. {Count(rows.Count)} left.");
                    Reselect(box, position);
                    break;

                default:
                    break;
            }
        }

        void OnAddRow(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ObjectListFieldState rows) return;

            var row = rows.AddRow();
            announce($"Added an entry to {rows.Label}.");
            Edit(rows, row);
        }

        void OnEditRow(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ListBox { DataContext: ObjectListFieldState rows, SelectedItem: GroupFieldState row })
                return;

            Edit(rows, row);
        }

        void Edit(ObjectListFieldState list, GroupFieldState row)
            => FieldGroupWindow.Show(window, $"{list.Label}, entry {list.Rows.IndexOf(row) + 1}", row, windows);

        // The entry box beside Add keeps meaning "the next one" and nothing else; an entry already
        // in the list is changed in a window of its own, named for which entry it is, the way a
        // row of an object list always has been. Before 1.1 the only way to change one was to
        // remove it and type it again — an environment variable's value included.
        void OnEditEntry(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is not ListBox { DataContext: ListFieldState list, SelectedItem: FieldState entry })
                return;

            FieldGroupWindow.Show(window, $"{list.Label}, entry {list.Items.IndexOf(entry) + 1}", [entry], windows,
                closeOnEnter: true);
        }
    }

    /// <summary>
    /// Puts the caret back in the box beside the Add button that has just disabled itself.
    /// </summary>
    /// <remarks>
    /// Only when the command came from the button. Pressing <kbd>Enter</kbd> in the box raises the
    /// same command, and there the caret is already where it belongs.
    /// </remarks>
    private static void ReturnToEntryBox(object? source)
    {
        if (source is not Button { Parent: Panel row } button) return;

        var box = row.Children.OfType<Control>().FirstOrDefault(c => !ReferenceEquals(c, button));
        box?.Focus();
    }

    /// <summary>
    /// Tells WPF's command system that a list's Add button has just become usable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CanExecute</c> is re-evaluated when <see cref="CommandManager"/> decides to ask, which is
    /// on input and on idle — and not when a view-model raises <c>PropertyChanged</c>. So choosing
    /// a value in a list's box and pressing <kbd>Tab</kbd> in the same breath asked the question in
    /// the wrong order: the button was still disabled at the instant Tab decided where to go, and a
    /// disabled button is not in the tab order, so Tab went past the one control the box's own help
    /// text had just told the user to press (phase5-notes 4.9).
    /// </para>
    /// <para>
    /// Only <c>Humcp.App</c> can do this. <see cref="ListFieldState.CanAdd"/> is a fact about a
    /// form and <c>Humcp.Schema</c> has never heard of WPF (§6.1); the notification exists there,
    /// and what it means to a command system is known only here.
    /// </para>
    /// </remarks>
    private static void KeepAddButtonsCurrent(IEnumerable<FieldState> fields)
    {
        foreach (var list in fields.SelectMany(f => f.SelfAndDescendants()).OfType<ListFieldState>())
        {
            list.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ListFieldState.CanAdd)) CommandManager.InvalidateRequerySuggested();
            };
        }
    }

    /// <summary>The state a command's parameter names, whether it arrived directly or as a list box.</summary>
    private static object? Entry(CanExecuteRoutedEventArgs e) => e.Parameter switch
    {
        ListBox box => box.DataContext,
        var other => other,
    };

    private static object? Selected(CanExecuteRoutedEventArgs e) =>
        e.Parameter is ListBox box ? box.SelectedItem : null;

    /// <summary>
    /// Keeps the caret in the list after a removal. Without this the selection is lost and focus
    /// falls back to the top of the list box, which is a long way to arrow back from.
    /// </summary>
    private static void Reselect(ListBox box, int index)
    {
        if (box.Items.Count == 0) return;

        box.SelectedIndex = Math.Min(index, box.Items.Count - 1);
        box.Focus();
    }

    private static string Count(int count) => $"{count} {(count == 1 ? "entry" : "entries")}";
}

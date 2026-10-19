using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// A table of results, as a real grid (ROADMAP §10, Phase 4).
/// </summary>
/// <remarks>
/// <para>
/// A grid is the one presentation that gives a screen reader user column headers on every cell —
/// arrow across a row and each value arrives with the name of the column it is in, which is a thing
/// no list of "name: value" lines can do without saying the name every time.
/// </para>
/// <para>
/// <b>Both kinds of virtualization are off</b>, for the reason §7.6 gives about forms: a virtualized
/// row is not in the UI Automation tree at all, so a grid that scrolls is a grid whose contents a
/// screen reader cannot count. That is also why <see cref="ResultBuildOptions.MaxRows"/> exists and
/// is small — the cap and this setting are two halves of one decision, and a grid that says "200 of
/// 40,000 rows" is honest where a grid that quietly holds forty thousand is neither honest nor
/// usable.
/// </para>
/// </remarks>
public class ResultDataGrid : DataGrid
{
    public ResultDataGrid()
    {
        AutoGenerateColumns = false;
        IsReadOnly = true;
        CanUserAddRows = false;
        CanUserDeleteRows = false;
        CanUserResizeRows = false;
        SelectionUnit = DataGridSelectionUnit.Cell;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        EnableRowVirtualization = false;
        EnableColumnVirtualization = false;
        GridLinesVisibility = DataGridGridLinesVisibility.All;

        // §7.23, in the row this time. A DataGridRow's accessible name is the plain text of the
        // object it is bound to, so without this every row announces a record's ToString. Set in
        // the object as well (ResultRow.ToString) — the rule says both, because either one alone is
        // a fix somebody removes later without hearing what it did.
        var rows = new Style(typeof(DataGridRow));
        rows.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(ResultRow.Summary))));
        RowStyle = rows;

        // A header click sorts by our rule rather than WPF's text comparison, and says so.
        Sorting += (_, e) =>
        {
            e.Handled = true;
            var index = Columns.IndexOf(e.Column);
            if (index >= 0) SortBy(index, ascending: _sort?.Column != index || !_sort.Ascending);
        };

        // A menu that exists before it is asked for, filled in when it opens. Without one, WPF did
        // not open anything for the grid, and Shift+F10 fell through to the window's system menu —
        // "Restore, Move, Size" where sorting was promised (phase6-notes).
        ContextMenu = new ContextMenu();
        ContextMenuOpening += OnContextMenuOpening;

        // The result view's status line, found once the grid is in it. A grid in a window of its
        // own has none, and falls back to a notification (see Say).
        Loaded += (_, _) => Announce ??= FindAncestor<ResultView>(this)?.Announce;
    }

    private ResultSort? _sort;

    /// <summary>
    /// Says what a sort did. Set by the result view to its own status line; a grid with nowhere to
    /// say it raises a UI Automation notification instead, so a sort is never silent (phase4-notes 6).
    /// </summary>
    public Action<string>? Announce { get; set; }

    /// <summary>
    /// Sorts the rows by one column, puts the caret at the top of that column, and says so — in the
    /// status line once, and in the column's header for as long as it is true (§7.24).
    /// </summary>
    public void SortBy(int column, bool ascending)
    {
        if (Table is not { } table || column < 0 || column >= Columns.Count) return;

        // New rows in the new order, rather than the same rows re-sorted in a view. Found by ear
        // (phase6-notes): after an in-place sort, Up and Down moved the caret and NVDA said nothing
        // at all, while Left and Right still spoke — the grid's automation peers went on describing
        // rows that had moved. A freshly bound grid is the state that is known to read correctly.
        _sort = new ResultSort(column, ascending);
        ItemsSource = table.Rows.Order(_sort).ToList();

        for (var i = 0; i < Columns.Count; i++)
        {
            Columns[i].SortDirection = i == column
                ? ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending
                : null;
            Columns[i].Header = ResultSort.Header(table.Columns[i].Label, i == column ? ascending : null);
        }

        MoveToTop(column);
        Say(ResultSort.Describe(table.Columns[column].Label, ascending));
    }

    /// <summary>Puts the rows back in the order the server sent them.</summary>
    public void ClearSort()
    {
        if (Table is not { } table) return;

        _sort = null;
        ItemsSource = table.Rows;

        for (var i = 0; i < Columns.Count; i++)
        {
            Columns[i].SortDirection = null;
            Columns[i].Header = table.Columns[i].Label;
        }

        MoveToTop(CurrentColumn is { } current ? Columns.IndexOf(current) : 0);
        Say("Back in the order the server sent.");
    }

    private void MoveToTop(int column)
    {
        if (Items.Count == 0 || column < 0) return;

        var item = Items[0];
        CurrentCell = new DataGridCellInfo(item, Columns[column]);
        SelectedCells.Clear();
        SelectedCells.Add(CurrentCell);
        ScrollIntoView(item, Columns[column]);

        // The rows were just rebuilt, so the cell the caret was in no longer exists; put it on the
        // new one, or WPF leaves it nowhere (§7.25).
        UpdateLayout();
        if (Columns[column].GetCellContent(item)?.Parent is DataGridCell cell) cell.Focus();
    }

    private void Say(string text)
    {
        if (Announce is { } announce)
        {
            announce(text);
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent, text, "humcp.sort");
    }

    /// <summary>
    /// The <kbd>Applications</kbd> key's menu, built for the column the caret is in: sorting is the
    /// thing a header click does and the keyboard has no header to click.
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Table is not { } table || Columns.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var index = CurrentColumn is { } current ? Columns.IndexOf(current) : 0;
        if (index < 0) index = 0;

        var label = table.Columns[index].Label;
        var menu = ContextMenu ??= new ContextMenu();
        menu.Items.Clear();

        menu.Items.Add(Item($"Sort by {label}, _ascending", () => SortBy(index, ascending: true)));
        menu.Items.Add(Item($"Sort by {label}, _descending", () => SortBy(index, ascending: false)));

        if (_sort is not null) menu.Items.Add(Item("_Original order", ClearSort));
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    /// <summary>
    /// Gives the grid a current cell as soon as it has focus.
    /// </summary>
    /// <remarks>
    /// The same defect as a list box that does not select what it focuses (phase3-notes 4.8), and it
    /// bites harder here: a <c>DataGrid</c> with no current cell does not handle an arrow key at
    /// all, so WPF treats the press as directional navigation and moves focus <em>out</em> of the
    /// grid — into the window's menu bar, silently. Two hundred rows on screen, none of them
    /// reachable. Found by ear in the first Phase 4 pass.
    /// </remarks>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (CurrentCell.IsValid || Items.Count == 0 || Columns.Count == 0) return;

        CurrentCell = new DataGridCellInfo(Items[0], Columns[0]);

        if (SelectionUnit != DataGridSelectionUnit.FullRow && !SelectedCells.Contains(CurrentCell))
            SelectedCells.Add(CurrentCell);
    }

    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(ResultNode), typeof(ResultDataGrid),
        new PropertyMetadata(null, (d, e) => ((ResultDataGrid)d).Rebuild(e.NewValue as ResultNode)));

    /// <summary>The node to show. Setting it rebuilds the columns.</summary>
    public ResultNode? Table
    {
        get => (ResultNode?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    private void Rebuild(ResultNode? table)
    {
        Columns.Clear();
        ItemsSource = null;
        _sort = null;

        if (table is null) return;

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];

            Columns.Add(new DataGridTextColumn
            {
                Header = column.Label,
                Binding = new Binding($"Cells[{i}].Text"),
                IsReadOnly = true,
                MaxWidth = 420,
            });
        }

        ItemsSource = table.Rows;

        AutomationProperties.SetName(this, table.AccessibleName);
        AutomationProperties.SetHelpText(this, HelpFor(table));
    }

    /// <summary>
    /// What the grid says about itself, including the two facts a grid cannot show: that some rows
    /// were left out, and that a cell reading "3 fields" has a whole value behind it.
    /// </summary>
    internal static string HelpFor(ResultNode table)
    {
        var parts = new List<string>();

        if (table.HelpText.Length > 0) parts.Add(table.HelpText);

        parts.Add(table.Rows.Count < table.TotalRows
            ? $"Showing the first {table.Rows.Count:N0} of {table.TotalRows:N0} rows."
            : "Arrow keys move from cell to cell.");

        if (table.Rows.Any(r => r.Cells.Any(c => c.HasDetail)))
            parts.Add("A cell that says how many fields or items it holds opens in a window with Enter.");

        if (table.Rows.Count > 1) parts.Add("The Applications key sorts by the column the caret is in.");

        return string.Join(" ", parts);
    }
}

/// <summary>
/// A table in a window of its own — what a table nested inside a result opens as.
/// </summary>
/// <remarks>
/// The same decision as a row of an object list getting its own window on the way in (§5, Tier 2):
/// a grid inside a tree inside a pane is three things to be lost among, and a window has a name
/// that is announced on entry and a boundary obvious from either side.
/// </remarks>
internal static class GridWindow
{
    public static void Show(Window owner, string title, ResultNode table, WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(windows);

        var grid = new ResultDataGrid { Table = table, Margin = new Thickness(8) };

        var layout = new DockPanel();
        layout.Children.Add(grid);

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 860,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout,
        };

        // §7.17: without this the window announces its entire content as its name.
        AutomationProperties.SetName(window, title);

        windows.Track(window);

        var bar = windows.CreateMenuBar(window, closable: true);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Insert(0, bar);

        window.Loaded += (_, _) => grid.Focus();
        window.Show();
    }
}

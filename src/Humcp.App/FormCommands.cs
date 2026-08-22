using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// The commands a generated form's templates raise. A <see cref="DataTemplate"/> has no code
/// behind it, so a button inside one asks by command and the window that owns the form answers.
/// </summary>
/// <remarks>
/// Each one carries the control it came from as its parameter rather than a view-model, which is
/// what lets a list's Remove know <em>which</em> entry without the list keeping a "selected" of its
/// own. Selection belongs to the list box; putting it in the state layer would be moving a fact
/// about a window into a place that is not supposed to know windows exist.
/// </remarks>
public static class FormCommands
{
    public static readonly RoutedUICommand Submit = new("Run", nameof(Submit), typeof(FormCommands),
        new InputGestureCollection { new KeyGesture(Key.Enter, ModifierKeys.Control) });

    public static readonly RoutedUICommand Details = new("Details", nameof(Details), typeof(FormCommands),
        new InputGestureCollection { new KeyGesture(Key.F1) });

    public static readonly RoutedUICommand NextPane = new("Next pane", nameof(NextPane), typeof(FormCommands),
        new InputGestureCollection { new KeyGesture(Key.F6) });

    public static readonly RoutedUICommand PreviousPane = new("Previous pane", nameof(PreviousPane),
        typeof(FormCommands), new InputGestureCollection { new KeyGesture(Key.F6, ModifierKeys.Shift) });

    public static readonly RoutedUICommand ToggleOptional = new("Show optional parameters",
        nameof(ToggleOptional), typeof(FormCommands), new InputGestureCollection { new KeyGesture(Key.O, ModifierKeys.Control) });

    /// <summary>Moves the entry box's value into a list. Parameter: the <c>ListFieldState</c>.</summary>
    public static readonly RoutedUICommand AddEntry = new("Add", nameof(AddEntry), typeof(FormCommands));

    /// <summary>Removes the selected entry. Parameter: the list box it is selected in.</summary>
    public static readonly RoutedUICommand RemoveEntry = new("Remove", nameof(RemoveEntry), typeof(FormCommands));

    /// <summary>Adds a row to an object list and opens it. Parameter: the <c>ObjectListFieldState</c>.</summary>
    public static readonly RoutedUICommand AddRow = new("Add entry", nameof(AddRow), typeof(FormCommands));

    /// <summary>Opens the selected row in its own window. Parameter: the list box.</summary>
    public static readonly RoutedUICommand EditRow = new("Edit entry", nameof(EditRow), typeof(FormCommands));

    /// <summary>Jumps from the problem list to the field it is about. Parameter: the list box.</summary>
    public static readonly RoutedUICommand GoToProblem = new("Go to field", nameof(GoToProblem), typeof(FormCommands));
}

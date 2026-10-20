using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// The commands a generated form's templates raise. A <see cref="DataTemplate"/> has no code
/// behind it, so a button inside one asks by command and the window that owns the form answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every <c>Text</c> is empty on purpose</b> (§7.27): WPF falls back to a bound command's
/// <c>Text</c> for a button's <c>AcceleratorKey</c>, so a command called "Run" makes the Run button
/// announce "Run" where a screen reader says the keyboard shortcut. The real keystroke is set on
/// the button instead, spelled out the way §7.15 asks.
/// </para>
/// <para>
/// Each one carries the control it came from as its parameter rather than a view-model, which is
/// what lets a list's Remove know <em>which</em> entry without the list keeping a "selected" of its
/// own. Selection belongs to the list box; putting it in the state layer would be moving a fact
/// about a window into a place that is not supposed to know windows exist.
/// </para>
/// </remarks>
public static class FormCommands
{
    public static readonly RoutedUICommand Submit = new("", nameof(Submit), typeof(FormCommands),
        new InputGestureCollection { new KeyGesture(Key.Enter, ModifierKeys.Control) });

    /// <summary>Stops the call that is running. Escape does the same while one is.</summary>
    public static readonly RoutedUICommand Stop = new("", nameof(Stop), typeof(FormCommands));

    public static readonly RoutedUICommand Details = new("", nameof(Details), typeof(FormCommands),
        new InputGestureCollection { new KeyGesture(Key.F1) });

    public static readonly RoutedUICommand NextPane = new("", nameof(NextPane), typeof(FormCommands),
        new InputGestureCollection { new KeyGesture(Key.F6) });

    public static readonly RoutedUICommand PreviousPane = new("", nameof(PreviousPane),
        typeof(FormCommands), new InputGestureCollection { new KeyGesture(Key.F6, ModifierKeys.Shift) });

    public static readonly RoutedUICommand ToggleOptional = new("",
        nameof(ToggleOptional), typeof(FormCommands), new InputGestureCollection { new KeyGesture(Key.O, ModifierKeys.Control) });

    /// <summary>Moves the entry box's value into a list. Parameter: the <c>ListFieldState</c>.</summary>
    public static readonly RoutedUICommand AddEntry = new("", nameof(AddEntry), typeof(FormCommands));

    /// <summary>Removes the selected entry. Parameter: the list box it is selected in.</summary>
    public static readonly RoutedUICommand RemoveEntry = new("", nameof(RemoveEntry), typeof(FormCommands));

    /// <summary>
    /// Opens the selected entry of a list of values in a window of its own, to change it in place.
    /// Parameter: the list box.
    /// </summary>
    public static readonly RoutedUICommand EditEntry = new("", nameof(EditEntry), typeof(FormCommands));

    /// <summary>Adds a row to an object list and opens it. Parameter: the <c>ObjectListFieldState</c>.</summary>
    public static readonly RoutedUICommand AddRow = new("", nameof(AddRow), typeof(FormCommands));

    /// <summary>Opens the selected row in its own window. Parameter: the list box.</summary>
    public static readonly RoutedUICommand EditRow = new("", nameof(EditRow), typeof(FormCommands));

    /// <summary>Jumps from the problem list to the field it is about. Parameter: the list box.</summary>
    public static readonly RoutedUICommand GoToProblem = new("", nameof(GoToProblem), typeof(FormCommands));
}

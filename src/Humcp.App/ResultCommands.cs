using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// The commands a rendered result raises (ROADMAP §10, Phase 4). The counterpart of
/// <see cref="FormCommands"/>, and for the same reason: a <see cref="System.Windows.DataTemplate"/>
/// has no code behind it, so a button inside one asks and the window answers.
/// </summary>
/// <remarks>
/// <b>Every <c>Text</c> here is empty, and that is deliberate</b> (§7.27). WPF's
/// <c>ButtonBaseAutomationPeer</c> falls back to the bound command's <c>Text</c> for its
/// <c>AcceleratorKey</c> when none is set, so a command called "Save as" makes every button bound
/// to it announce <em>"Save as"</em> in the slot where a screen reader says the keyboard shortcut.
/// Heard in the first Phase 4 pass as "Save Image, PNG, 70 bytes, button, … Save as". Where a real
/// keystroke exists it is set on the button with <c>AutomationProperties.AcceleratorKey</c>;
/// where none exists, nothing should be said at all.
/// </remarks>
public static class ResultCommands
{
    /// <summary>Writes a part of a result to a file the user chooses. Parameter: the section.</summary>
    public static readonly RoutedUICommand Save = new("", nameof(Save), typeof(ResultCommands));

    /// <summary>
    /// Hands a part to whichever program Windows opens its type with. Parameter: the section.
    /// Only ever offered for a type <see cref="Humcp.Core.MediaTypes"/> recognises.
    /// </summary>
    public static readonly RoutedUICommand Open = new("", nameof(Open), typeof(ResultCommands));

    /// <summary>Follows a resource link, as far as <see cref="Humcp.Core.LinkPolicy"/> allows. Parameter: the section.</summary>
    public static readonly RoutedUICommand Follow = new("", nameof(Follow), typeof(ResultCommands));

    /// <summary>Puts a part, a node or a link on the clipboard. Parameter: whatever is to be copied.</summary>
    /// <remarks>
    /// No <c>InputGestures</c>: a gesture on the command would fire it from anywhere in the result
    /// with no parameter at all, which is a keystroke that does nothing. The tree and the grid bind
    /// <kbd>Control</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> themselves, with themselves as the
    /// parameter.
    /// </remarks>
    public static readonly RoutedUICommand Copy = new("", nameof(Copy), typeof(ResultCommands));

    /// <summary>
    /// Opens whatever the caret is on in a window of its own: a long string as text, a table as a
    /// grid, a nested value as a tree. Parameter: the tree, or the node.
    /// </summary>
    public static readonly RoutedUICommand Expand = new("", nameof(Expand), typeof(ResultCommands));
}

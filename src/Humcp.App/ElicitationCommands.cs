using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// The two answers a question window can give that are not "close it" (ROADMAP §10, Phase 5).
/// </summary>
/// <remarks>
/// Cancel is deliberately missing: cancelling is what closing the window means, so it is
/// <see cref="WindowManager.CloseWindow"/> and reaches the server through the same path whether it
/// arrived as <kbd>Escape</kbd>, <kbd>Ctrl</kbd>+<kbd>W</kbd>, the Cancel button or the title bar.
/// Three ways out that answer differently would be three ways to be surprised.
///
/// <para>
/// Both carry an empty <c>Text</c>, like every other command here: WPF announces a bound command's
/// <c>Text</c> in the slot a screen reader uses for the keyboard shortcut (§7.27).
/// </para>
/// </remarks>
public static class ElicitationCommands
{
    public static readonly RoutedUICommand Accept = new("", nameof(Accept), typeof(ElicitationCommands),
        new InputGestureCollection { new KeyGesture(Key.Enter, ModifierKeys.Control) });

    public static readonly RoutedUICommand Decline = new("", nameof(Decline), typeof(ElicitationCommands));
}

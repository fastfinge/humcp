using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// The container a generated form's fields live in — an <see cref="ItemsControl"/> with the three
/// defaults that make WPF's version of one unusable with a screen reader taken out (ROADMAP §7.12,
/// §7.13, §7.6).
/// </summary>
/// <remarks>
/// <para>
/// Every one of these was a live defect in the Phase 0 spike before it was a rule
/// (phase0-findings 2.1–2.3), and all three are invisible unless someone listens:
/// </para>
/// <list type="number">
/// <item><description>
/// A plain <c>ItemsControl</c> reports itself to UI Automation as a <b>List</b> and every generated
/// field as a <b>DataItem</b> named after the bound object's <c>ToString()</c>. A screen reader
/// announces "list, 8 items" and then a C# class name on each field. The peers below report groups
/// instead, with empty names, leaving the real controls as the only named things.
/// </description></item>
/// <item><description>
/// <c>Control.Focusable</c> defaults to true, so the container becomes a tab stop of its own —
/// <kbd>Tab</kbd> lands on an unnamed group before it reaches the first field.
/// </description></item>
/// <item><description>
/// UI virtualization removes fields from the automation tree altogether. Phase 0 measured 209
/// fields becoming 14. It is overridden here rather than set at each use, because a rule that has
/// to be remembered in three XAML files is a rule that will be forgotten in one of them.
/// </description></item>
/// </list>
/// </remarks>
public class FormItemsControl : ItemsControl
{
    static FormItemsControl()
    {
        FocusableProperty.OverrideMetadata(
            typeof(FormItemsControl), new FrameworkPropertyMetadata(false));

        VirtualizingPanel.IsVirtualizingProperty.OverrideMetadata(
            typeof(FormItemsControl), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FormItemsControlPeer(this);
}

/// <summary>
/// The list a field's entries live in — a <see cref="ListBox"/> that selects what it focuses.
/// </summary>
/// <remarks>
/// WPF moves focus into a list box's first item without selecting it, and a single-item list gives
/// the arrow keys nowhere to go, so the selection stays empty however long the user presses. Every
/// command that acts on "the selected entry" is then disabled, and <kbd>Delete</kbd> does nothing
/// at all — on a list whose own help text says Delete removes the selected one. A manual pass hit
/// exactly that and had to guess at <kbd>Space</kbd> (phase3-notes 4.8).
/// </remarks>
public class EntryListBox : ListBox
{
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (SelectedIndex < 0 && Items.Count > 0) SelectedIndex = 0;
    }
}

internal sealed class FormItemsControlPeer(ItemsControl owner) : ItemsControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => "GeneratedForm";

    protected override ItemAutomationPeer CreateItemAutomationPeer(object item)
        => new FormFieldItemPeer(item, this);
}

internal sealed class FormFieldItemPeer(object item, ItemsControlAutomationPeer parent)
    : ItemAutomationPeer(item, parent)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => "GeneratedField";

    /// <summary>Empty on purpose: the label and the control inside it carry the name.</summary>
    protected override string GetNameCore() => string.Empty;
}

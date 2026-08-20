using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Humcp.Spike;

/// <summary>
/// A plain WPF <see cref="ItemsControl"/> reports itself to UIA as a <c>List</c>, and each
/// generated field as a <c>DataItem</c> named after the bound object's <c>ToString()</c>. A screen
/// reader therefore announces a generated form as "list, 8 items" and reads out the type name of
/// the view-model on every field.
///
/// Phase 0 finding: templated form generation needs a custom automation peer. This one reports the
/// container and each row as unnamed groups, so the only things with names are the actual controls.
/// </summary>
public class FormItemsControl : ItemsControl
{
    static FormItemsControl()
    {
        // Second Phase 0 finding: Control.Focusable defaults to true, so a bare ItemsControl
        // becomes a tab stop of its own. Tab then lands on an unnamed group before reaching the
        // first field — twice over, once for the form and once for each nested object group.
        FocusableProperty.OverrideMetadata(
            typeof(FormItemsControl), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FormItemsControlPeer(this);
}

internal sealed class FormItemsControlPeer : ItemsControlAutomationPeer
{
    public FormItemsControlPeer(ItemsControl owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => "GeneratedForm";

    protected override ItemAutomationPeer CreateItemAutomationPeer(object item)
        => new FormFieldItemPeer(item, this);
}

internal sealed class FormFieldItemPeer : ItemAutomationPeer
{
    public FormFieldItemPeer(object item, ItemsControlAutomationPeer parent) : base(item, parent) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => "GeneratedField";

    /// <summary>Empty on purpose: the label and the control inside carry the name.</summary>
    protected override string GetNameCore() => string.Empty;
}

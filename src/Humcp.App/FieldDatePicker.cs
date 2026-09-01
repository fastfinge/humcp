using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// The <see cref="DatePicker"/> a generated form uses, with the properties set on it copied onto
/// the box that actually takes the focus (ROADMAP §7, proposed 7.32).
/// </summary>
/// <remarks>
/// <para>
/// A <c>DatePicker</c> is a composite: focus lands on <c>PART_TextBox</c> inside it, and a screen
/// reader reads that box's own automation properties. Name and help text survive, because a peer
/// with neither falls back to its parent's — but <c>IsRequiredForForm</c> and the access key do
/// not, so a required date announced *"Departure date, edit"* where every other required field on
/// the same form announced *"required"* (phase5-notes 4.3).
/// </para>
/// <para>
/// Nothing about that is specific to dates. It is what happens to any property set on a control
/// whose focus lands on one of its parts, and the fix is to put the properties where the focus is.
/// </para>
/// </remarks>
public class FieldDatePicker : DatePicker
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Mirror();
    }

    /// <summary>
    /// Copies what the box needs. Called again on load because the style's bindings — which is
    /// where <c>IsRequiredForForm</c> comes from — have not necessarily produced a value by the
    /// time the template is applied.
    /// </summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => Mirror();
    }

    private void Mirror()
    {
        if (GetTemplateChild("PART_TextBox") is not TextBox box) return;

        AutomationProperties.SetIsRequiredForForm(box, AutomationProperties.GetIsRequiredForForm(this));
        AutomationProperties.SetItemStatus(box, AutomationProperties.GetItemStatus(this));

        if (DataContext is not FieldState { AccessKeyIndex: { } index } field
            || index < 0 || index >= field.Label.Length)
        {
            return;
        }

        // The access key itself stays on the picker, where the Label that targets it puts it, and
        // pressing it works: focus lands here, in the box. What does not work is *hearing* which
        // letter it is — the letter is worked out inside the picker's peer and the reader is
        // looking at the box, and neither setting the property on the box nor re-pointing the
        // Label at it changed that. Both were tried; §7.15 is the rule that covers what is left,
        // and it is the same rule that puts the Alt plus Down Arrow hint in this sentence.
        var key = char.ToUpperInvariant(field.Label[index]).ToString();
        var help = AutomationProperties.GetHelpText(this);

        AutomationProperties.SetHelpText(box, $"{help} Alt plus {key} moves here.".TrimStart());
    }
}

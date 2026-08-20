using System.Windows;
using System.Windows.Controls;

namespace Humcp.Spike;

/// <summary>
/// The mechanism the real app will use (ROADMAP §10, Phase 3). Included in the spike because
/// templated, item-generated controls are exactly where WPF accessibility might disappoint —
/// LabeledBy across template scopes, and virtualization hiding fields from the UIA tree.
/// </summary>
public sealed class FieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? MultilineTemplate { get; set; }
    public DataTemplate? NumberTemplate { get; set; }
    public DataTemplate? BoolTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }
    public DataTemplate? DateTemplate { get; set; }
    public DataTemplate? DateAsTextTemplate { get; set; }
    public DataTemplate? GroupTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is not SpikeField f ? base.SelectTemplate(item, container) : f.Kind switch
        {
            FieldKind.Text => TextTemplate,
            FieldKind.Multiline => MultilineTemplate,
            FieldKind.Number => NumberTemplate,
            FieldKind.Bool => BoolTemplate,
            FieldKind.Enum => EnumTemplate,
            FieldKind.Date => DateTemplate,
            FieldKind.DateAsText => DateAsTextTemplate,
            FieldKind.Group => GroupTemplate,
            _ => TextTemplate,
        };
}

using System.Windows;
using System.Windows.Controls;

namespace Humcp.App;

/// <summary>
/// Picks the control for one part of a result — <see cref="FieldTemplateSelector"/>'s counterpart
/// on the way back (ROADMAP §10, Phase 4).
/// </summary>
/// <remarks>
/// The same mechanism for the same reason: the mapping from a shape of data to a WPF control lives
/// in one file, and adding a presentation means adding a <c>DataTemplate</c> to
/// <c>ResultTemplates.xaml</c> and a line here. The phase's exit criterion is that every kind of
/// content has one, so the list below is the checklist.
/// </remarks>
public sealed class ResultTemplateSelector : DataTemplateSelector
{
    public string Prefix { get; set; } = "Result";

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not ResultSection section || container is not FrameworkElement element) return null;

        return element.TryFindResource($"{Prefix}.{Name(section)}") as DataTemplate
               ?? element.TryFindResource($"{Prefix}.Text") as DataTemplate;
    }

    private static string Name(ResultSection section) => section switch
    {
        TextSection => "Text",
        MediaSection => "Media",
        LinkSection => "Link",
        SummarySection => "Summary",

        // A table is a grid, and everything else is a tree. Both are built from the same
        // ResultNode; which one a value gets is decided by its shape and nothing else.
        StructuredSection { IsTable: true } => "Table",
        StructuredSection => "Tree",

        _ => "Text",
    };
}

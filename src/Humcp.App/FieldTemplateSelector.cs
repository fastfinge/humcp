using System.Windows;
using System.Windows.Controls;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// Picks the control for one field — the mechanism ROADMAP §10 names for Phase 3, and the one the
/// Phase 0 spike was built around so that the framework decision was taken against the real thing.
/// </summary>
/// <remarks>
/// <para>
/// The mapping from <see cref="FieldKind"/> to a WPF control lives here and nowhere else.
/// <c>FieldKind</c> is named for the shape of the data rather than for a control precisely so that
/// this file is the only one that has to change when a tier decides differently, or when a second
/// capability source arrives that has never heard of WPF (§6.1, hedge 2).
/// </para>
/// <para>
/// Templates are found by key rather than assigned property by property, so adding a control means
/// adding a <c>DataTemplate</c> to <c>FieldTemplates.xaml</c> and nothing else.
/// </para>
/// </remarks>
public sealed class FieldTemplateSelector : DataTemplateSelector
{
    /// <summary>Which set of templates to draw from. One set today; the parameter is what a second would use.</summary>
    public string Prefix { get; set; } = "Field";

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not FieldState state || container is not FrameworkElement element) return null;

        return element.TryFindResource($"{Prefix}.{Name(state)}") as DataTemplate
               ?? element.TryFindResource($"{Prefix}.Text") as DataTemplate;
    }

    /// <summary>
    /// The template name for one field. Two of these are decisions rather than lookups, and both
    /// were measured before they were made.
    /// </summary>
    private static string Name(FieldState state) => state switch
    {
        // A parameter with values behind it becomes a combo box the drop-down can be arrowed
        // through; one without stays a plain text box. MCP cannot autocomplete a tool argument at
        // all (§4, pitfall 3), and 48.8% of the corpus's top-level parameters are plain text, so
        // this is the single most-used control in the application.
        // A prompt argument the server can complete (Phase 12) is a drop-down from the start, and
        // says where its values come from — which is not only this user's past.
        TextFieldState { HasServerSuggestions: true } => "Suggestions",

        // A tool's parameter that wants an address offers the server's resources (Phase 13), and
        // says so: "values you have used before" would not be true of them.
        TextFieldState { OffersResources: true } => "Resources",

        TextFieldState { HasHistory: true } => "History",

        TextFieldState text => text.Field.Kind switch
        {
            FieldKind.MultilineText => "Multiline",
            FieldKind.Raw => "Raw",
            _ => "Text",
        },

        ChoiceFieldState => "Choice",
        BooleanFieldState => "Boolean",
        DateFieldState date => date.HasTime ? "DateTime" : "Date",
        // A list of a closed set of values gets a drop-down to add from rather than a text box.
        ListFieldState list => list.Draft is ChoiceFieldState ? "ListOfChoices" : "List",

        GroupFieldState => "Group",
        ObjectListFieldState => "ObjectList",
        _ => "Text",
    };
}

using System.Globalization;
using System.Text;

namespace Humcp.Schema.Tests;

/// <summary>
/// Renders a <see cref="FormModel"/> as text, so a golden file is something a person can read and a
/// diff is something a person can judge. A serialised object graph would be exact and unreviewable;
/// the point of the goldens is that a change in tiering is visible at a glance.
/// </summary>
public static class FormReport
{
    public static string Render(string toolName, FormModel form)
    {
        var sb = new StringBuilder();

        sb.Append("### ").Append(toolName).AppendLine();

        if (form.Title.Length > 0) sb.Append("    title: ").AppendLine(form.Title);
        if (form.DegradedReason is { } reason) sb.Append("    whole form is raw: ").AppendLine(reason);
        if (form.IsEmpty) sb.AppendLine("    (no arguments)");

        foreach (var field in form.Fields) Render(sb, field, depth: 1);

        return sb.ToString();
    }

    private static void Render(StringBuilder sb, FieldModel field, int depth)
    {
        var indent = new string(' ', depth * 2);
        var name = field.Name.Length > 0 ? field.Name : "(item)";

        sb.Append(indent)
          .Append(name.PadRight(Math.Max(1, 34 - indent.Length)))
          .Append(' ')
          .Append(field.Kind.ToString().PadRight(14))
          .Append(" T").Append((int)field.Tier);

        if (field.IsRequired) sb.Append(" required");
        if (field.AllowsNull) sb.Append(" nullable");
        if (field.IsDeprecated) sb.Append(" deprecated");
        if (field.IsReadOnly) sb.Append(" read-only");

        sb.AppendLine();

        var detail = indent + "    ";

        if (field.Label != Fallback(field.Name)) sb.Append(detail).Append("label: ").AppendLine(field.Label);
        if (field.HelpText.Length > 0) sb.Append(detail).Append("help: ").AppendLine(field.HelpText);
        if (field.DegradedReason is { } reason) sb.Append(detail).Append("raw because: ").AppendLine(reason);

        if (field.Constraints.Describe() is { Length: > 0 } limits)
            sb.Append(detail).Append("limits: ").AppendLine(limits);

        if (field.Constraints.Pattern is { } pattern)
            sb.Append(detail).Append("pattern: ").AppendLine(pattern);

        if (field.Default is { } value)
            sb.Append(detail).Append("default: ").AppendLine(value.GetRawText());

        if (field.Choices.Count > 0) RenderChoices(sb, field, detail);

        foreach (var child in field.Fields) Render(sb, child, depth + 1);

        if (field.ItemField is { } item && field.Kind != FieldKind.ObjectList) Render(sb, item, depth + 1);
    }

    private static void RenderChoices(StringBuilder sb, FieldModel field, string detail)
    {
        // Long enumerations are real - one server ships 60 country codes - and the whole list in a
        // golden file is noise. The count is the part that would change silently.
        var shown = field.Choices.Take(12)
            .Select(c => c.Label == c.ValueText ? c.ValueText : $"{c.ValueText}={c.Label}");

        sb.Append(detail)
          .Append("choices (").Append(field.Choices.Count.ToString(CultureInfo.InvariantCulture)).Append("): ")
          .Append(string.Join(" | ", shown))
          .AppendLine(field.Choices.Count > 12 ? " | …" : "");
    }

    private static string Fallback(string name) =>
        name.Length == 0 ? "Item" : NameHumanizer.Humanize(name);
}

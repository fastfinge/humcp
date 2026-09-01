using System.Text;
using System.Text.Json;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// Everything about one generated field, in full, for the window <kbd>F1</kbd> opens
/// (ROADMAP §4, pitfall 1: the first sentence is the help text and the rest waits behind a
/// keystroke).
/// </summary>
/// <remarks>
/// Shared rather than duplicated, because a generated form is a generated form wherever it came
/// from: the same text answers <kbd>F1</kbd> on a tool's own parameters and on a question a server
/// asked in the middle of running one. A habit that stops working at a window boundary is a habit
/// nobody keeps.
/// </remarks>
internal static class FieldDetails
{
    public static string Describe(FieldState field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var text = new StringBuilder()
            .AppendLine(field.Label)
            .AppendLine(new string('-', 60))
            .AppendLine($"Sent as:     {(field.Path.Length == 0 ? "(the whole argument object)" : field.Path)}")
            .AppendLine($"Kind:        {field.Field.Kind}")
            .AppendLine($"Tier:        {(int)field.Field.Tier} ({field.Field.Tier})")
            .AppendLine($"Required:    {(field.IsRequired ? "yes" : "no")}")
            .AppendLine($"Empty means: {(field.Field.AllowsNull ? "null" : "this parameter is not sent")}")
            .AppendLine();

        if (field.Field.DegradedReason is { Length: > 0 } reason)
            text.AppendLine($"Shown as JSON because the schema describes {reason}.").AppendLine();

        if (!field.Field.Constraints.IsEmpty)
            text.AppendLine("Limits").AppendLine(field.Field.Constraints.Describe()).AppendLine();

        if (field.Status is { Length: > 0 } status)
            text.AppendLine("Problem").AppendLine(status).AppendLine();

        text.AppendLine("Description")
            .AppendLine(field.Field.FullDescription.Length > 0
                ? field.Field.FullDescription
                : "(the server supplied none)")
            .AppendLine()
            .AppendLine("Schema")
            .AppendLine(Pretty(field.Field.Schema));

        return text.ToString();
    }

    public static string Pretty(JsonElement schema)
    {
        try
        {
            return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "(could not be formatted: " + ex.Message + ")";
        }
    }
}

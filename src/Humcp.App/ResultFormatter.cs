using System.Text;
using System.Text.Json;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// What a result <em>says</em>, as opposed to what it looks like: the sentence announced when a
/// call finishes, and the whole thing written out as text for the details window and the clipboard.
/// </summary>
/// <remarks>
/// Since Phase 4 the result is a set of controls rather than a wall of text (see
/// <see cref="ResultView"/>), so this file no longer decides what is on screen. It decides the two
/// things that are still sentences — and the announcement is the more important of them, because it
/// is the only part of a result a user hears without going to look for it.
/// </remarks>
internal static class ResultFormatter
{
    /// <summary>
    /// The whole result as text: every part, the structured content as JSON, and the schema it was
    /// read against. This is what <kbd>F1</kbd> shows and what Copy copies — the honest, complete,
    /// unrendered version, one keystroke from the rendered one.
    /// </summary>
    public static string Render(ToolInfo? tool, ToolCallOutcome outcome, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var sb = new StringBuilder();
        var name = tool?.DisplayName ?? "Result";

        sb.AppendLine($"{name} — {(outcome.IsError ? "error" : "complete")} at {when.ToLocalTime():HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine();

        if (outcome.Blocks.Count == 0) sb.AppendLine("The tool returned no content.");

        for (var i = 0; i < outcome.Blocks.Count; i++)
        {
            var block = outcome.Blocks[i];

            sb.AppendLine(outcome.Blocks.Count == 1
                ? block.Heading
                : $"Part {i + 1} of {outcome.Blocks.Count}: {block.Heading}");

            switch (block)
            {
                case TextResultBlock text:
                    sb.AppendLine(text.Text);
                    break;

                case ResourceTextResultBlock resource:
                    sb.AppendLine(resource.Text);
                    break;

                case LinkResultBlock link:
                    sb.AppendLine(link.Uri);
                    if (link.Description.Length > 0) sb.AppendLine(link.Description);
                    break;

                case UnknownResultBlock unknown:
                    sb.AppendLine(unknown.Json);
                    break;

                default:
                    break;
            }

            sb.AppendLine();
        }

        if (outcome.Structured is { } structured)
        {
            sb.AppendLine(outcome.StructuredRepeatsText
                ? "Structured content (the same text again, in the wrapper the output schema asks for):"
                : "Structured content:");

            sb.AppendLine(Pretty(structured));
            sb.AppendLine();
        }

        if (tool is { OutputSchema: { } schema })
        {
            sb.AppendLine("Output schema:");
            sb.AppendLine(Pretty(schema));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// What the status line says when a call finishes. UI Automation has no way to announce "that
    /// finished", so this is the live region's job — and it is the whole of what a user hears
    /// before deciding whether to go and read the result (§7's core rule).
    /// </summary>
    public static string Announcement(ToolCallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.IsError)
        {
            var first = outcome.CombinedText.Split('\n', 2)[0].Trim();

            return first.Length > 0
                ? $"Tool returned an error. {TextSanitizer.Clean(first, 200)}"
                : "Tool returned an error.";
        }

        var parts = outcome.Blocks.Select(Describe).Where(p => p.Length > 0).ToList();

        var sb = new StringBuilder("Tool call complete. ");

        sb.Append(parts.Count == 0 ? "No content" : Capitalise(string.Join(", ", parts)));
        sb.Append('.');

        if (outcome.HasOwnStructuredContent) sb.Append(" A structured result as well.");
        else if (outcome.StructuredRepeatsText) sb.Append(" The structured result repeats the text.");

        sb.Append(" Press F6 to review the result.");
        return sb.ToString();
    }

    /// <summary>
    /// A value as indented JSON. Deliberately the raw element rather than a rendered node: this is
    /// the complete version of the result, and a renderer's simplifications have no business in it.
    /// </summary>
    private static string Pretty(JsonElement value)
    {
        try
        {
            return JsonSerializer.Serialize(value, Indented);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return $"(could not be formatted: {ex.Message})";
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>One part, in the fewest words that say what it is and how much of it there is.</summary>
    private static string Describe(ResultBlock block) => block switch
    {
        TextResultBlock text => $"{Count(Lines(text.Text), "line", "lines")} of text",
        MediaResultBlock media => media.Phrase,
        LinkResultBlock link => link.Label.Length > 0 ? $"a link to {link.Label}" : "a resource link",
        ResourceTextResultBlock resource => $"an embedded resource, {Count(Lines(resource.Text), "line", "lines")}",
        UnknownResultBlock unknown => $"a part of type {unknown.Kind}",
        _ => "",
    };

    private static int Lines(string text) => text.Length == 0 ? 0 : text.Split('\n').Length;

    /// <summary>
    /// The list of parts starts a sentence, and each part is written to sit inside one — so a
    /// result that is only a picture would be announced as "Tool call complete. an image" without
    /// this.
    /// </summary>
    private static string Capitalise(string text) =>
        text.Length == 0 || !char.IsLower(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Count(int count, string singular, string plural) =>
        $"{count:N0} {(count == 1 ? singular : plural)}";
}

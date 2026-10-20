using System.Text.Json;

namespace Humcp.Schema;

/// <summary>
/// JSON a server sent as text: found, parsed, and handed to <see cref="ResultBuilder"/> like any
/// structured result (humcp 1.1).
/// </summary>
/// <remarks>
/// <para>
/// Most servers never send <c>structuredContent</c>. They serialise their answer and send it as a
/// text part, and 1.0 showed that as text — which for a weather forecast is one line of three
/// thousand characters, with 48 temperatures in one array and the 48 hours they belong to in
/// another. Readable by eye with effort, and not at all by ear. Open-Meteo sends every answer this
/// way; the MLB server sends a title line and then indented JSON. Both are in
/// <c>tests/fixtures/results</c>.
/// </para>
/// <para>
/// <b>It is strict on purpose, because it is a guess about text the server called text.</b> The
/// JSON has to run to the end of the text, it has to be an object or an array — a bare number is
/// already readable — and the only thing allowed in front of it is a short title. Anything else
/// stays text exactly as it was, and the text as it arrived is always one keystroke away under
/// <kbd>F1</kbd>. The worst case is a tree where the server meant prose, and that case still has
/// the prose.
/// </para>
/// </remarks>
public static class JsonText
{
    /// <summary>How many lines of title may stand in front of the JSON.</summary>
    public const int MaxTitleLines = 3;

    private static readonly JsonDocumentOptions Options = new()
    {
        // The parser's own guard against nesting deep enough to exhaust the stack; a document past
        // it stays text. ResultBuilder has a shallower limit of its own for what it draws.
        MaxDepth = 64,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>
    /// Reads <paramref name="text"/> as JSON, or as a title followed by JSON, and says whether it
    /// could.
    /// </summary>
    /// <param name="text">A text part, already through <see cref="TextSanitizer.CleanMultiline"/>.</param>
    /// <param name="title">
    /// The lines in front of the JSON, joined and without a trailing colon — "Tracked Teams" — or
    /// empty when the text was JSON from its first character.
    /// </param>
    /// <param name="value">The parsed object or array, detached from the document it was read from.</param>
    public static bool TryRead(string? text, out string title, out JsonElement value)
    {
        title = "";
        value = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var preamble = new List<string>();

        for (var start = 0; start < lines.Length; start++)
        {
            var line = lines[start].Trim();

            if (line.StartsWith('{') || line.StartsWith('['))
            {
                if (TryParse(string.Join('\n', lines[start..]), out value))
                {
                    title = Title(preamble);
                    return true;
                }
            }

            if (line.Length == 0) continue;

            // A title is a line or three, not a paragraph; past that it is prose that happens to
            // have JSON after it, and the user is better served by the prose as it was written.
            if (preamble.Count == MaxTitleLines || line.Length > TextSanitizer.NameLength) return false;
            preamble.Add(line);
        }

        return false;
    }

    private static bool TryParse(string json, out JsonElement value)
    {
        value = default;

        try
        {
            using var document = JsonDocument.Parse(json, Options);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return false;

            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Title(List<string> lines) =>
        TextSanitizer.Clean(string.Join(" ", lines), TextSanitizer.NameLength).TrimEnd(':', ' ');
}

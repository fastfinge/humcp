using System.Text.Json;

namespace Humcp.Schema.Tests;

/// <summary>
/// Real tool results, captured from published servers (<c>tests/fixtures/results</c>). The same
/// rule as <see cref="Corpus"/>: nobody here chose what is in them.
/// </summary>
public static class ResultFixtures
{
    public static string Directory => Path.Combine(Corpus.Directory, "..", "results");

    /// <summary>
    /// The first text part of one captured result, sanitised the way <c>ServerConnection</c>
    /// sanitises it — so a test reads exactly what the renderer is given.
    /// </summary>
    public static string Text(string server, string tool)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, server, tool + ".json")));

        var text = document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        return TextSanitizer.CleanMultiline(text, TextSanitizer.ResultLength);
    }
}

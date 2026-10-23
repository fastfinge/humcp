using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humcp.TestServer;

/// <summary>
/// The fixture's resources (ROADMAP §11, Phase 13): text with lines in it, a picture, JSON that the
/// result view should read as a structure, and a template whose one variable the server completes.
/// </summary>
internal static class TestResources
{
    public const string NotesUri = "fixture://notes/readme";
    public const string PictureUri = "fixture://pictures/square";
    public const string ForecastUri = "fixture://data/forecast";
    public const string NoteTemplate = "fixture://notes/{name}";

    /// <summary>Three resources at fixed addresses and one template.</summary>
    public const int Count = 4;

    private static readonly Dictionary<string, string> Notes = new(StringComparer.Ordinal)
    {
        ["readme"] = "Read me first.\nThis note has three lines.\nThe third is this one.",
        ["shopping"] = "Bread\nMilk\nBraille paper",
        ["todo"] = "Write the Phase 13 notes.",
    };

    public static IEnumerable<McpServerResource> All()
    {
        yield return McpServerResource.Create(
            () => Notes["readme"],
            new McpServerResourceCreateOptions
            {
                UriTemplate = NotesUri,
                Name = "readme",
                Title = "Read me",
                Description = "A note of three lines. Exists so a text resource can be reviewed line by line.",
                MimeType = "text/plain",
            });

        yield return McpServerResource.Create(
            () => BlobResourceContents.FromBytes(TestTools.TinyPng(), PictureUri, "image/png"),
            new McpServerResourceCreateOptions
            {
                UriTemplate = PictureUri,
                Name = "square",
                Title = "A red square",
                Description = "A picture, read as a resource rather than returned by a tool.",
                MimeType = "image/png",
            });

        yield return McpServerResource.Create(
            () => new TextResourceContents
            {
                Uri = ForecastUri,
                MimeType = "application/json",
                Text = """{"hours":["09:00","10:00","11:00"],"temperature":[11,13,14]}""",
            },
            new McpServerResourceCreateOptions
            {
                UriTemplate = ForecastUri,
                Name = "forecast",
                Title = "A forecast",
                Description = "JSON, which a client can show as a table.",
                MimeType = "application/json",
            });

        yield return McpServerResource.Create(
            ([Description("Which note.")] string name) =>
                Notes.TryGetValue(name, out var text) ? text : $"There is no note called {name}.",
            new McpServerResourceCreateOptions
            {
                UriTemplate = NoteTemplate,
                Name = "note",
                Title = "A note by name",
                Description = "Any of the fixture's notes. The server suggests their names.",
                MimeType = "text/plain",
            });
    }

    /// <summary>The note names, for <c>completion/complete</c> on the template's one variable.</summary>
    public static IEnumerable<string> NoteNames => Notes.Keys.Order(StringComparer.Ordinal);
}

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// One part of a result as a window needs it: a heading, a name for whatever control carries it,
/// and the part itself.
/// </summary>
/// <remarks>
/// These are the result's answer to <see cref="FieldState"/>, and they stop where that one does.
/// Anything that is a <em>rule</em> — what a value is called, whether a string is long enough to
/// want a window, whether structured content repeats the text — lives in <c>Humcp.Schema</c> and is
/// tested without a desktop (§6.3). What is here is what genuinely needs WPF: a decoded picture,
/// and a command a button can be bound to.
/// </remarks>
internal abstract class ResultSection(string heading)
{
    public string Heading { get; } = heading;

    /// <summary>The accessible name of the control that carries this part.</summary>
    public virtual string AutomationName => Heading;

    /// <summary>Settable after the fact, so that an error can be added to the part that carries it.</summary>
    public string HelpText { get; set; } = "";
}

/// <summary>Text, in the read-only caret-navigable box §7.9 requires.</summary>
internal sealed class TextSection(string heading, string text, string uri = "") : ResultSection(heading)
{
    public string Text { get; } = text;

    /// <summary>Where the text came from, for an embedded resource. Empty for plain text.</summary>
    public string Uri { get; } = uri;
}

/// <summary>Bytes: an image with a preview where one can safely be made, a sound, or a file.</summary>
internal sealed class MediaSection : ResultSection, System.ComponentModel.INotifyPropertyChanged
{
    private string? _altText;

    /// <summary>
    /// Past this many pixels a preview is not attempted. A few hundred kilobytes of PNG can decode
    /// to hundreds of megabytes of bitmap, and the bytes came from a third party (§4, pitfall 5).
    /// The part is still saved, opened and described; only the picture on screen is skipped.
    /// </summary>
    private const long MaxPreviewPixels = 40_000_000;

    public MediaSection(string heading, MediaResultBlock block) : base(heading)
    {
        Block = block;
        Preview = MakePreview(block);
    }

    public MediaResultBlock Block { get; }

    /// <summary>The picture, for the half of this audience that has some sight and is using it (§7.20).</summary>
    public ImageSource? Preview { get; }

    public string Description => Block.Description;

    public bool CanOpen => Block.CanOpen;

    /// <summary>
    /// Whether the local model can be asked about it (Phase 10): an image that decoded. One that will
    /// not decode for WPF is one the model's own decoder is unlikely to read either, and asking would
    /// spend half a minute to say so.
    /// </summary>
    public bool CanDescribe => Block.Kind == MediaKind.Image && Preview is not null;

    /// <summary>
    /// What the local model said, or is doing, or why it is not — <see cref="ImageDescription"/>'s
    /// words. Null until a result view decides, and then <see cref="Explanation"/> says it instead of
    /// "there is no description".
    /// </summary>
    public string? AltText
    {
        get => _altText;
        set
        {
            if (_altText == value) return;
            _altText = value;
            PropertyChanged?.Invoke(this, new(nameof(AltText)));
            PropertyChanged?.Invoke(this, new(nameof(Explanation)));
        }
    }

    /// <summary>True once the model has said something, so a second Describe does not ask it the same question again.</summary>
    public bool IsDescribed { get; set; }

    /// <summary>True while the model is working on it.</summary>
    public bool IsDescribing { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public override string AutomationName => Block.Uri.Length > 0
        ? $"{Block.Description}, {Block.Uri}"
        : Block.Description;

    /// <summary>
    /// What the buttons cannot say. An image has no alt text anywhere in the protocol (§4, pitfall
    /// 6), so this is the whole of what is known about it — and where humcp will not open it, the
    /// reason it will not (§7.18).
    /// </summary>
    public string Explanation => (Block.CanOpen, AltText) switch
    {
        (true, { } alt) => $"{Description}. {alt}",
        (true, null) => $"{Description}. There is no description of it: the protocol has nowhere to put one.",
        _ => $"{Description}. humcp does not recognise this type, so it can be saved but not opened.",
    };

    private static ImageSource? MakePreview(MediaResultBlock block)
    {
        if (block.Kind != MediaKind.Image || !MediaTypes.CanOpen(block.MimeType)) return null;

        try
        {
            using var probe = new MemoryStream(block.Data.ToArray());
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.None, BitmapCacheOption.None);

            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPreviewPixels) return null;

            using var stream = new MemoryStream(block.Data.ToArray());
            var image = new BitmapImage();

            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException
                                      or FileFormatException or OverflowException or IOException)
        {
            // A picture that will not decode is still a file worth saving, and "image, PNG, 45 KB"
            // is still true about it. Only the preview is lost.
            return null;
        }
    }
}

/// <summary>A pointer to something the server has and did not send.</summary>
internal sealed class LinkSection(string heading, LinkResultBlock block) : ResultSection(heading)
{
    public LinkResultBlock Block { get; } = block;

    public string Uri => Block.Uri;

    public bool CanOpen => Block.CanOpen;

    public string ActionName => LinkPolicy.ActionName(Block.Uri);

    /// <summary>What following it would do, or why nothing will (§7.18).</summary>
    public string Explanation
    {
        get
        {
            var parts = new List<string>();

            if (Block.Description.Length > 0) parts.Add(Block.Description);
            if (Block.MimeType.Length > 0) parts.Add(Block.MimeType);
            if (Block.Size is { } size) parts.Add(MediaTypes.DescribeSize(size));

            parts.Add(LinkPolicy.Describe(Block.Uri));

            return Sentences(parts);
        }
    }

    /// <summary>
    /// Joins parts into sentences without doubling the punctuation. A server's own description
    /// usually ends in a full stop and the words after it do not, and "the revision this server
    /// speaks.. text/html" is what a synthesiser makes of the difference.
    /// </summary>
    private static string Sentences(IEnumerable<string> parts) => string.Join(" ", parts
        .Select(p => p.Trim())
        .Where(p => p.Length > 0)
        .Select(p => p[^1] is '.' or '!' or '?' or ':' ? p : p + "."));

    public override string AutomationName =>
        Block.Label.Length > 0 ? $"Resource link, {Block.Label}" : "Resource link";
}

/// <summary>
/// The structured result, taken apart by <see cref="ResultBuilder"/>: a grid when the value is a
/// table, and a tree when it is anything else.
/// </summary>
internal sealed class StructuredSection(string heading, ResultNode root) : ResultSection(heading)
{
    public ResultNode Root { get; } = root;

    public bool IsTable => Root.Shape == ResultShape.Table;

    /// <summary>The root as a one-item sequence, because a tree binds to items rather than to one.</summary>
    public IReadOnlyList<ResultNode> Roots { get; } = [root];

    public override string AutomationName => $"{Heading}, {Root.Summary}";
}

/// <summary>Turns one outcome into the parts a window shows (ROADMAP §10, Phase 4).</summary>
internal static class ResultSections
{
    public static IReadOnlyList<ResultSection> Build(ToolInfo? tool, ToolCallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var sections = new List<ResultSection>();
        var count = outcome.Blocks.Count;

        for (var i = 0; i < count; i++)
        {
            var block = outcome.Blocks[i];

            // Numbered only when there is more than one. A single text result is the overwhelmingly
            // common case, and it reads best as "Result" — which is what it has been since Phase 1.
            var heading = count == 1 ? block.Heading : $"{block.Heading}, part {i + 1} of {count}";

            var label = count == 1 && block is TextResultBlock ? "Result" : heading;

            if (block switch
                {
                    TextResultBlock text => ReadAsJson(tool, outcome, label, text.Text),
                    ResourceTextResultBlock resource => ReadAsJson(tool, outcome, label, resource.Text),
                    _ => null,
                } is { } read)
            {
                sections.Add(read);
                continue;
            }

            sections.Add(block switch
            {
                TextResultBlock text => new TextSection(count == 1 ? "Result" : heading, text.Text)
                {
                    HelpText = text.IsTruncated
                        ? $"This was cut short at {text.Text.Length:N0} characters; " +
                          $"the server sent {text.OriginalLength:N0}."
                        : "Arrow keys review the text line by line.",
                },

                MediaResultBlock media => new MediaSection(heading, media),

                LinkResultBlock link => new LinkSection(heading, link),

                ResourceTextResultBlock resource => new TextSection(heading, resource.Text, resource.Uri)
                {
                    HelpText = "Arrow keys review the text line by line.",
                },

                UnknownResultBlock unknown => new TextSection(heading, unknown.Json)
                {
                    HelpText = "This build has no presentation for this kind of result, so it is " +
                               "shown as the JSON the server sent.",
                },

                _ => new TextSection(heading, block.Heading),
            });
        }

        // Structured content, when it says something the text did not. When it does not — which is
        // what 13 of the corpus's 25 output schemas describe — the announcement says so and the raw
        // JSON stays one keystroke away, rather than one window saying everything twice.
        if (outcome.HasOwnStructuredContent && outcome.Structured is { } structured)
        {
            var root = ResultBuilder.Build(tool?.OutputSchema, structured, "Structured result");

            sections.Add(new StructuredSection("Structured result", root)
            {
                HelpText = tool is { DescribesItsOutput: true }
                    ? "The tool published a schema for this, so these names are the server's own."
                    : "The tool published no schema for this, so these names come from the data.",
            });
        }

        if (sections.Count == 0)
            sections.Add(new TextSection("Result", "The tool returned no content.")
            {
                HelpText = "The call finished and sent nothing back to show.",
            });

        // §7.24: a fact that is only on screen is a fact somebody has not been told. An error is
        // announced when the call finishes and written into the window's heading, and it also goes
        // into the help text of the first part — which is where a user arriving at the result a
        // minute later, by Tab or by F6, will actually hear it.
        if (outcome.IsError)
            sections[0].HelpText = $"The tool reported an error. {sections[0].HelpText}".TrimEnd();

        return sections;
    }

    /// <summary>
    /// A text part that is JSON, as the tree or grid it would have been had the server sent it as
    /// structured content — or null, and it stays text (see <see cref="JsonText"/>).
    /// </summary>
    /// <remarks>
    /// When the structured content is the same value, that is what is drawn, with the tool's
    /// output schema: same data, and the server's own names for it rather than names made from the
    /// data. The structured section is then not drawn a second time, because
    /// <see cref="ToolCallOutcome.HasOwnStructuredContent"/> already says it repeats the text.
    /// </remarks>
    private static StructuredSection? ReadAsJson(ToolInfo? tool, ToolCallOutcome outcome, string heading, string text)
    {
        if (!JsonText.TryRead(text, out var title, out var value)) return null;

        var (source, schema) = outcome.StructuredRepeatsText && outcome.Structured is { } structured
            ? (structured, tool?.OutputSchema)
            : (value, null);

        var label = title.Length > 0 ? title : heading;

        return new StructuredSection(label, ResultBuilder.Build(schema, source, label))
        {
            HelpText = "The server sent this as text, and humcp has read it as JSON. " +
                       "F1 shows the text as it arrived.",
        };
    }
}

using System.Text;
using System.Text.Json;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// One piece of a result. Every kind a server can send has one of these and a presentation to go
/// with it (ROADMAP §10, Phase 4) — the phase's exit criterion is that none of them degenerates
/// into nothing.
/// </summary>
/// <remarks>
/// No protocol vocabulary crosses this boundary (§6.1, hedge 2): what reaches the window is a
/// picture, a sound, a link and a piece of text, not an <c>ImageContentBlock</c>.
/// </remarks>
public abstract record ResultBlock
{
    /// <summary>What this part is called where a window needs a heading for it.</summary>
    public abstract string Heading { get; }
}

/// <summary>Text, ready for the read-only result box (ROADMAP §7.9).</summary>
/// <param name="Text">The text, sanitised for review rather than for speech (§7.19).</param>
/// <param name="OriginalLength">
/// How long it was before <see cref="TextSanitizer.ResultLength"/> bit, or the same number when it
/// did not. A truncated result that does not say so is a result the user believes is complete.
/// </param>
public sealed record TextResultBlock(string Text, int OriginalLength = 0) : ResultBlock
{
    public bool IsTruncated => OriginalLength > Text.Length;

    public override string Heading => IsTruncated
        ? $"Text, {Text.Length:N0} of {OriginalLength:N0} characters"
        : "Text";
}

/// <summary>Which of the three shapes bytes arrived in, and what can be done with them.</summary>
public enum MediaKind
{
    Image,
    Audio,
    File,
}

/// <summary>
/// Bytes: an image, a sound, or the contents of an embedded resource.
/// </summary>
/// <remarks>
/// ROADMAP §4, pitfall 6: an image has no alt text anywhere in the protocol, so what an honest
/// client can say is what it <em>is</em> — kind, type, size — and then offer to save it or open it.
/// v2's <c>IResultSummarizer</c> seam is where a description would come from; nothing here guesses
/// one.
/// </remarks>
public sealed record MediaResultBlock(
    MediaKind Kind,
    string MimeType,
    ReadOnlyMemory<byte> Data,
    string Uri = "") : ResultBlock
{
    /// <summary>"Image, PNG, 45 KB" — the whole of what is known about it.</summary>
    public string Description => MediaTypes.Describe(Kind, MimeType, Data.Length);

    /// <summary>The same, as a phrase for the middle of a sentence: "an image, PNG, 45 KB".</summary>
    public string Phrase => MediaTypes.Phrase(Kind, MimeType, Data.Length);

    public override string Heading => Uri.Length > 0 ? $"{Description}, {Uri}" : Description;

    /// <summary>The file name to offer, with an extension humcp chose (see <see cref="MediaTypes"/>).</summary>
    public string SuggestedFileName => MediaTypes.SuggestedFileName(Kind, MimeType);

    /// <summary>Whether humcp will hand this file to the shell once it has been written.</summary>
    public bool CanOpen => MediaTypes.CanOpen(MimeType);
}

/// <summary>
/// A pointer to something the server has but did not send — <c>resource_link</c>.
/// </summary>
public sealed record LinkResultBlock(
    string Uri,
    string Label,
    string Description,
    string MimeType,
    long? Size) : ResultBlock
{
    public override string Heading =>
        Label.Length > 0 ? $"Resource link, {Label}" : "Resource link";

    /// <summary>Whether humcp will hand this URI to the shell (see <see cref="LinkPolicy"/>).</summary>
    public bool CanOpen => LinkPolicy.Action(Uri) != LinkAction.None;

    public LinkAction Action => LinkPolicy.Action(Uri);
}

/// <summary>An embedded resource that came as text — the common case for files and documents.</summary>
public sealed record ResourceTextResultBlock(string Uri, string MimeType, string Text) : ResultBlock
{
    public override string Heading =>
        Uri.Length > 0 ? $"Embedded resource, {Uri}" : "Embedded resource";
}

/// <summary>
/// A part of a kind this build has never heard of. Shown as its JSON rather than dropped: a client
/// generated from other people's metadata always has edges, and at an edge silence is
/// indistinguishable from a bug (§7.18).
/// </summary>
public sealed record UnknownResultBlock(string Kind, string Json) : ResultBlock
{
    public override string Heading => $"Result part of type {Kind}";
}

/// <summary>
/// The outcome of one call, with the protocol left behind.
/// </summary>
/// <remarks>
/// <see cref="Structured"/> is carried and not interpreted, because interpreting it needs the
/// tool's <c>outputSchema</c> as well and the renderer takes the two together (§6.1, hedge 5).
/// </remarks>
public sealed record ToolCallOutcome(
    bool IsError,
    IReadOnlyList<ResultBlock> Blocks,
    JsonElement? Structured = null,
    bool NeedsMoreInput = false)
{
    /// <summary>A failure that never reached the server, or came back as a transport-level error.</summary>
    public static ToolCallOutcome Failure(string message)
        => new(true, [new TextResultBlock(message)]);

    /// <summary>Every text part, joined — what the announcement counts and what a duplicate structured result is compared against.</summary>
    public string CombinedText
    {
        get
        {
            var sb = new StringBuilder();

            foreach (var text in Blocks.OfType<TextResultBlock>())
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(text.Text);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// True when the structured content is the text content again in a wrapper, which is what 15 of
    /// the corpus's 25 output schemas describe (see <see cref="ResultBuilder.RepeatsText"/>).
    /// </summary>
    public bool StructuredRepeatsText =>
        Structured is { } structured && ResultBuilder.RepeatsText(structured, CombinedText);

    /// <summary>Structured content worth showing beside the text, which is not all of it.</summary>
    public bool HasOwnStructuredContent =>
        Structured is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null }
        && !StructuredRepeatsText;
}

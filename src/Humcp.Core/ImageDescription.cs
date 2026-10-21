using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// What humcp says about an image the local model has described, or is describing, or cannot
/// (ROADMAP §11, Phase 10). Words only, so they are tested here rather than heard for the first time.
/// </summary>
/// <remarks>
/// <b>Everything a model writes is announced as generated</b> (§11.0). A user who cannot see the
/// picture cannot check the description, so it must never arrive with the authority of something the
/// server sent: it is labelled where it is read, and labelled where it is announced. The source — the
/// picture itself — stays one keystroke away, behind Open.
/// </remarks>
public static class ImageDescription
{
    /// <summary>A description longer than this is cut: it is 120 words asked for, and this is far past that.</summary>
    public const int MaxLength = GeneratedText.MaxLength;

    public const string Describing = "Describing it with the local model. This takes a while on the processor; the description will appear here.";

    public const string NotDownloaded =
        "There is no description of it: the protocol has nowhere to put one. File, Local model downloads a model that can describe images on this computer.";

    public const string Off =
        "There is no description of it: the protocol has nowhere to put one. Control plus D describes it with the local model.";

    /// <summary>The description, as the box beside the picture holds it: labelled before a word of it is read.</summary>
    public static string Generated(string text) => GeneratedText.Label(text);

    /// <summary>What the status line says when a description is ready. Says where it is, and what it is.</summary>
    public static string Ready(string phrase)
        => $"Description of {phrase} is ready, in the result. It is generated, so it can be wrong.";

    public static string Failed(string reason)
        => $"The image could not be described. {TextSanitizer.Clean(reason, TextSanitizer.HelpLength)}";
}

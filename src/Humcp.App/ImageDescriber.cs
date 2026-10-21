using Humcp.Core;
using Humcp.Inference;

namespace Humcp.App;

/// <summary>
/// Alt text for the images tools return (ROADMAP §11, Phase 10; §4 pitfall 6): the shared model, the
/// setting that says whether to describe without being asked, and nothing else. One per application,
/// handed to every result view.
/// </summary>
/// <remarks>
/// Not <c>IResultSummarizer</c>. The interface §6 named in v1 waits, like <c>IToolSuggester</c>, for a
/// second implementation to say what its shape is (§12); summaries arrive in Phase 11, and that is
/// where the two meet.
/// </remarks>
internal sealed class ImageDescriber(ModelHost host, AppSettings settings)
{
    /// <summary>Whether an image is described as it arrives: the setting is on and there is a model to ask.</summary>
    public bool DescribesAutomatically => settings.DescribeImages && host.IsDownloaded;

    public bool IsDownloaded => host.IsDownloaded;

    /// <summary>
    /// The model's description of <paramref name="image"/>, in its own words — the caller labels it
    /// (<see cref="ImageDescription.Generated"/>). Throws <see cref="LocalModelException"/> with a
    /// sentence when there is no model or it cannot read the image.
    /// </summary>
    public async Task<string> DescribeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellation)
    {
        var model = await host.GetAsync().ConfigureAwait(false);
        var description = await model.DescribeImageAsync(image, cancellation).ConfigureAwait(false);

        return description.Text.Length > 0
            ? description.Text
            : throw new LocalModelException("The model said nothing about it.");
    }
}

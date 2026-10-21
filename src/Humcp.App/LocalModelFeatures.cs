using Humcp.Core;
using Humcp.Inference;

namespace Humcp.App;

/// <summary>
/// What the local model does for the user (ROADMAP §11, Phases 10–11): alt text for images, tool
/// search that understands the request, and summaries of long results. One per application, over the
/// one shared model, handed to every window that offers any of them.
/// </summary>
/// <remarks>
/// Not <c>IResultSummarizer</c>. That seam stays a name until something other than this model
/// summarises (§12) — tool search earned <see cref="IToolSuggester"/> because word matching was
/// already a second implementation; summaries and descriptions have no second one.
/// </remarks>
internal sealed class LocalModelFeatures(ModelHost host, AppSettings settings)
{
    /// <summary>Summaries are 150 words asked for; room to finish the sentence.</summary>
    private const int SummaryTokens = 320;

    public bool IsDownloaded => host.IsDownloaded;

    /// <summary>Whether an image is described as it arrives: the setting is on and there is a model to ask.</summary>
    public bool DescribesAutomatically => settings.DescribeImages && host.IsDownloaded;

    /// <summary>Tool search by the model (Phase 11), which reads the catalogue the first time it is asked.</summary>
    public ModelToolSuggester Suggester { get; } = new(host);

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

    /// <summary>
    /// A summary of <paramref name="outcome"/>, labelled (<see cref="ResultSummary.Generated"/>).
    /// Throws <see cref="LocalModelException"/> as <see cref="DescribeAsync"/> does.
    /// </summary>
    public async Task<string> SummariseAsync(string toolName, ToolCallOutcome outcome, CancellationToken cancellation)
    {
        var (request, cut) = ResultSummary.Request(toolName, outcome);

        var model = await host.GetAsync().ConfigureAwait(false);
        var summary = await model.GenerateAsync(request, SummaryTokens, null, cancellation).ConfigureAwait(false);

        return summary.Text.Length > 0
            ? ResultSummary.Generated(summary.Text, cut)
            : throw new LocalModelException("The model said nothing about it.");
    }
}

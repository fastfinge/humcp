namespace Humcp.Core;

/// <summary>
/// "What do you want to do?" → the tools that would do it, best first (ROADMAP §6's seam, built in
/// Phase 11). Two implementations, which is what earned it an interface (§12): words in common
/// (<see cref="LexicalToolSuggester"/>, instant, on every machine) and the local model
/// (<c>Humcp.Inference.ModelToolSuggester</c>, which understands the request and needs a download).
/// </summary>
/// <remarks>
/// The catalogue is passed with every request rather than held: the suggester decides for itself
/// whether what it has already read is still the catalogue, which for the model is the difference
/// between a second and two minutes.
/// </remarks>
public interface IToolSuggester
{
    /// <summary>The entries that answer <paramref name="request"/>, best first. Possibly none.</summary>
    Task<IReadOnlyList<ToolSearchEntry>> SuggestAsync(
        string request, IReadOnlyList<ToolSearchEntry> catalogue, CancellationToken cancellation);
}

/// <summary>Phase 8's search behind the interface: every tool with a word in common, BM25-ranked.</summary>
public sealed class LexicalToolSuggester : IToolSuggester
{
    private IReadOnlyList<ToolSearchEntry>? _indexed;
    private ToolSearch _search = new([]);

    public Task<IReadOnlyList<ToolSearchEntry>> SuggestAsync(
        string request, IReadOnlyList<ToolSearchEntry> catalogue, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        if (!ReferenceEquals(catalogue, _indexed))
        {
            _search = new ToolSearch(catalogue);
            _indexed = catalogue;
        }

        IReadOnlyList<ToolSearchEntry> found = [.. _search.Search(request).Select(h => h.Entry)];
        return Task.FromResult(found);
    }
}

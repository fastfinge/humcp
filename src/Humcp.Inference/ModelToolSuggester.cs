using Humcp.Core;

namespace Humcp.Inference;

/// <summary>
/// Tool search by the local model (ROADMAP §11, Phase 11): the second <see cref="IToolSuggester"/>.
/// The whole catalogue is read once — the first time anyone asks, and again when the tools change —
/// and each request is then a few dozen tokens on the end of it, answered in about a second.
/// </summary>
/// <remarks>
/// Re-ranking the word matches cannot work, which is why it reads everything: 11 of the spike's 35
/// requests had no right answer in the lexical top fifteen to re-rank, because the request shares no
/// word with the tool (v2-llm-spike §6).
/// </remarks>
public sealed class ModelToolSuggester(ModelHost host) : IToolSuggester
{
    /// <summary>A question and three ids, with room to spare.</summary>
    private const int Room = 512;

    private const int AnswerTokens = 60;

    private readonly SemaphoreSlim _one = new(1, 1);
    private Prefill? _read;
    private LocalModel? _readBy;
    private string? _fingerprint;

    /// <summary>
    /// Whether <paramref name="catalogue"/> has been read by the model as it is now loaded — so the
    /// caller can say "reading the tool list first" only when it will.
    /// </summary>
    public bool HasRead(IReadOnlyList<ToolSearchEntry> catalogue)
        => _read is { IsDisposed: false } && host.IsLoaded && _fingerprint == ToolSuggestionPrompt.Fingerprint(catalogue);

    /// <summary>How long the last reading took, for saying how long the next will.</summary>
    public TimeSpan? LastReading { get; private set; }

    public async Task<IReadOnlyList<ToolSearchEntry>> SuggestAsync(
        string request, IReadOnlyList<ToolSearchEntry> catalogue, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (string.IsNullOrWhiteSpace(request) || catalogue.Count == 0) return [];

        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var model = await host.GetAsync().ConfigureAwait(false);
            var fingerprint = ToolSuggestionPrompt.Fingerprint(catalogue);

            if (_read is null || _read.IsDisposed || !ReferenceEquals(_readBy, model) || _fingerprint != fingerprint)
            {
                _read?.Dispose();

                // Not cancellable: a reading abandoned half-way is minutes thrown away, and the next
                // request would only start it again. The question is what gets cancelled.
                _read = await model.PrefillAsync(ToolSuggestionPrompt.Catalogue(catalogue), Room, CancellationToken.None)
                    .ConfigureAwait(false);
                _readBy = model;
                _fingerprint = fingerprint;
                LastReading = _read.Took;
            }

            var answer = await model.ContinueAsync(_read, ToolSuggestionPrompt.Question(request), AnswerTokens, cancellation)
                .ConfigureAwait(false);

            return ToolSuggestionPrompt.Parse(answer.Text, catalogue);
        }
        finally
        {
            _one.Release();
        }
    }
}

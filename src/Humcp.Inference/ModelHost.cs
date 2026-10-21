using Humcp.Core;

namespace Humcp.Inference;

/// <summary>
/// The one loaded model the whole application shares (ROADMAP §11, Phase 10): loaded the first time
/// anything asks, kept loaded, and let go only when the model is removed or humcp closes. The File
/// menu's check and every image description go through here, so there is never a second copy of
/// several gigabytes in memory.
/// </summary>
public sealed class ModelHost(ModelStore store, ModelManifest manifest) : IDisposable
{
    private readonly object _gate = new();
    private Task<LocalModel>? _loading;
    private bool _disposed;

    public ModelStore Store { get; } = store;

    public ModelManifest Manifest { get; } = manifest;

    /// <summary>Whether every file is here — not whether it has been loaded, which is the first request's job.</summary>
    public bool IsDownloaded => Store.StateOf(Manifest) == ModelState.Ready;

    /// <summary>Whether the model is in memory now, so a caller can say "loading" only when it will.</summary>
    public bool IsLoaded => _loading is { IsCompletedSuccessfully: true };

    /// <summary>The model, loaded on first use. Throws <see cref="LocalModelException"/> with a sentence when it cannot be.</summary>
    public Task<LocalModel> GetAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsDownloaded)
                throw new LocalModelException("The model is not downloaded. File, Local model, Download the model fetches it.");

            // A failed load is not kept: the next request tries again, which is what someone who has
            // just freed some memory would expect.
            if (_loading is null || _loading.IsFaulted || _loading.IsCanceled)
                _loading = LocalModel.LoadAsync(Store.FolderOf(Manifest), CancellationToken.None);

            return _loading;
        }
    }

    /// <summary>
    /// Lets the model go, after anything it is doing — so its files can be deleted once this
    /// completes. Off the caller's thread: a description in progress can take half a minute to
    /// finish, and the model is not disposed under it.
    /// </summary>
    public Task UnloadAsync()
    {
        Task<LocalModel>? loading;
        lock (_gate)
        {
            loading = _loading;
            _loading = null;
        }

        // Still loading: let it finish, then let it go, rather than leaving gigabytes nobody holds.
        return loading is null
            ? Task.CompletedTask
            : loading.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Dispose();
            }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _ = UnloadAsync();
    }
}

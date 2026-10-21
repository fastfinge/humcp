using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Humcp.Core;
using Humcp.Inference;

namespace Humcp.App;

/// <summary>
/// File, Local model (ROADMAP §11, Phase 9): download the pinned model, stop a download, check that
/// the model loads and answers, remove it, and read what it is. Nothing uses the model yet — alt
/// text is Phase 10 — so this is the whole of its surface, and the proof that the runtime ships.
/// </summary>
/// <remarks>
/// Menu items rather than a window of their own: each is one action with one sentence of answer in
/// the status line, and an item whose label carries the state ("Download the rest of the model, 1.2 of 2.9
/// gigabytes") is heard on arrow-through without opening anything.
/// </remarks>
internal sealed class LocalModelMenu : IDisposable
{
    private readonly ModelManifest _manifest = ModelCatalog.Cpu;
    private readonly ModelStore _store = ModelStore.Default();
    private readonly HttpClient _http;
    private readonly Window _owner;
    private readonly WindowManager _windows;
    private readonly Action<string> _say;
    private readonly Action<string> _sayIfAway;

    private readonly MenuItem _download = new();
    private readonly MenuItem _stop = new() { Header = "_Stop the download" };
    private readonly MenuItem _check = new() { Header = "_Check the model" };
    private readonly MenuItem _remove = new() { Header = "_Remove the model" };
    private readonly MenuItem _about = new() { Header = "_About the model…" };

    private CancellationTokenSource? _downloading;
    private LocalModel? _loaded;
    private bool _checking;

    /// <summary>A first Remove warns; the second removes. Choosing anything else in between is changing your mind.</summary>
    private bool _removing;

    public LocalModelMenu(MenuItem parent, Window owner, WindowManager windows, Action<string> say, Action<string> sayIfAway)
    {
        _owner = owner;
        _windows = windows;
        _say = say;
        _sayIfAway = sayIfAway;

        // No timeout: a 2.5 GB file on a slow line takes as long as it takes, and Stop is the way out.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"humcp/{typeof(LocalModelMenu).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"}");

        _download.Click += (_, _) => _ = DownloadAsync();
        _stop.Click += (_, _) => _downloading?.Cancel();
        _check.Click += (_, _) => _ = CheckAsync();
        _remove.Click += (_, _) => Remove();
        _about.Click += (_, _) => About();

        parent.Items.Add(_download);
        parent.Items.Add(_stop);
        parent.Items.Add(_check);
        parent.Items.Add(_remove);
        parent.Items.Add(new Separator());
        parent.Items.Add(_about);

        // Not reset when the menu closes: choosing Remove closes it, so the second Remove would only
        // ever warn again (found by ear, phase9-notes). Choosing anything else is changing your mind.
        parent.SubmenuOpened += (_, _) => Refresh();
        _about.Click += (_, _) => _removing = false;
        Refresh();
    }

    /// <summary>Labels and availability from the state on disk, read each time the menu opens.</summary>
    private void Refresh()
    {
        var state = _store.StateOf(_manifest);
        var busy = _downloading is not null;

        _download.Header = state switch
        {
            _ when busy => "_Downloading the model…",
            ModelState.Partial => $"_Download the rest of the model, {Gigabytes(_store.BytesOnDisk(_manifest))} of {Gigabytes(_manifest.TotalSize)} gigabytes",
            ModelState.Ready => "_Download the model (already downloaded)",
            _ => $"_Download the model, {Gigabytes(_manifest.TotalSize)} gigabytes",
        };

        _download.IsEnabled = !busy && state != ModelState.Ready;
        _stop.IsEnabled = busy;
        _check.IsEnabled = !busy && !_checking && state == ModelState.Ready;
        _remove.IsEnabled = !busy && !_checking && state != ModelState.Absent;
    }

    private async Task DownloadAsync()
    {
        if (_downloading is not null) return;

        _removing = false;
        _downloading = new CancellationTokenSource();
        var announcer = new ModelDownloadAnnouncer();

        // Reported on the downloader's thread; said on this one. Progress<T> captures the UI context.
        var progress = new Progress<ModelDownloadProgress>(report =>
        {
            if (_downloading is not null && announcer.Offer(report) is { } line) _say(line);
        });

        try
        {
            await new ModelDownloader(_http, _store).DownloadAsync(_manifest, progress, _downloading.Token);

            const string Done = "The model is downloaded, and every file matched its checksum. Check the model on the File menu tries it.";
            _say(Done);
            _sayIfAway(Done);
        }
        catch (OperationCanceledException)
        {
            _say($"Download stopped at {Gigabytes(_store.BytesOnDisk(_manifest))} of {Gigabytes(_manifest.TotalSize)} gigabytes. Downloading again carries on from there.");
        }
        catch (ModelDownloadException ex)
        {
            _say(ex.Message);
            _sayIfAway(ex.Message);
        }
        finally
        {
            _downloading.Dispose();
            _downloading = null;
        }
    }

    /// <summary>
    /// Loads the model if it is not loaded, and asks it one short question: the proof that the
    /// runtime is in the build and the files are the model, with the numbers a user would wait on.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking) return;

        _checking = true;
        _removing = false;

        try
        {
            if (_loaded is null)
            {
                _say("Loading the model.");
                _loaded = await LocalModel.LoadAsync(_store.FolderOf(_manifest), CancellationToken.None);
            }

            _say("The model is loaded. Asking it a question.");
            var answer = await _loaded.GenerateAsync(
                "In one short sentence, say what a screen reader is.", 40, null, CancellationToken.None);

            // Generated text, on its way to a speech synthesiser: the same hygiene as a server's (§4, pitfall 5).
            var line = $"The model answered: {Humcp.Schema.TextSanitizer.Clean(answer.Text, 300)} It took {Seconds(answer.FirstToken)} seconds to the first word " +
                       $"and wrote {answer.TokensPerSecond:0} tokens a second; loading took {Seconds(_loaded.LoadTime)} seconds.";
            _say(line);
            _sayIfAway("The model answered.");
        }
        catch (LocalModelException ex)
        {
            _say(ex.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    private void Remove()
    {
        if (!_removing)
        {
            _removing = true;
            _say($"Choose Remove the model again to delete it and free {Gigabytes(_store.BytesOnDisk(_manifest))} gigabytes. It can be downloaded again.");
            return;
        }

        _removing = false;

        // A loaded model holds its files open, and Windows will not delete a mapped file.
        _loaded?.Dispose();
        _loaded = null;

        _say(_store.Remove(_manifest) ?? "The model is removed.");
    }

    private void About()
    {
        var text = new StringBuilder()
            .AppendLine(_manifest.Name)
            .AppendLine(new string('-', 60))
            .AppendLine($"State:    {_store.StateOf(_manifest) switch
            {
                ModelState.Ready => "downloaded and checked",
                ModelState.Partial => $"partly downloaded, {Gigabytes(_store.BytesOnDisk(_manifest))} of {Gigabytes(_manifest.TotalSize)} gigabytes",
                _ => "not downloaded",
            }}")
            .AppendLine($"Size:     {Gigabytes(_manifest.TotalSize)} gigabytes")
            .AppendLine($"Licence:  {_manifest.Licence}, {_manifest.LicenceUrl}")
            .AppendLine($"Source:   {_manifest.SourceUrl}")
            .AppendLine($"Folder:   {_store.FolderOf(_manifest)}")
            // One line per paragraph: the window wraps it, and a line broken here as well was read
            // part-sentence by part-sentence, some parts twice (phase9-notes).
            .AppendLine()
            .AppendLine("The model runs on this computer's processor, through ONNX Runtime GenAI on Windows ML. What you " +
                        "give it stays on this computer. Windows ML is Microsoft's, under Microsoft's licence terms, and may " +
                        "send Microsoft information about its use; the user guide's privacy section says more.")
            .AppendLine()
            .AppendLine("Every file is checked against the SHA-256 below as it arrives, and a file that does not match is " +
                        "deleted rather than used.")
            .AppendLine();

        foreach (var file in _manifest.Files)
            text.AppendLine($"{file.Path}  {file.Size:N0} bytes  {file.Sha256}");

        TextWindow.Show(_owner, "About the model", text.ToString(), _windows);
    }

    private static string Gigabytes(long bytes) => (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    public void Dispose()
    {
        _downloading?.Cancel();
        _loaded?.Dispose();
        _http.Dispose();
    }
}

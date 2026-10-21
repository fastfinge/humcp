using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>How far a download has got, in bytes, across every file of the model.</summary>
public readonly record struct ModelDownloadProgress(long Done, long Total)
{
    public int Percent => Total <= 0 ? 100 : (int)Math.Min(100, Done * 100 / Total);
}

/// <summary>A download that could not finish, with a sentence that says why and what to do.</summary>
public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Fetches a pinned model into a <see cref="ModelStore"/>, checking every file against its SHA-256 as
/// it arrives (ROADMAP §11.0). A stopped download carries on from where it stopped.
/// </summary>
/// <remarks>
/// Each file is written as <c>name.partial</c> and hashed while it is written; only a file whose hash
/// matches is given its own name, and one that does not is deleted. Nothing reads a model file that
/// has not matched, which is the whole of what makes downloading one from a third party acceptable.
/// </remarks>
public sealed class ModelDownloader(HttpClient http, ModelStore store)
{
    private const int BufferSize = 1 << 20;

    /// <summary>Room to leave on the disk beyond the model itself, so the download does not fill it.</summary>
    public const long DiskHeadroom = 1L << 30;

    public async Task DownloadAsync(ModelManifest model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(model);

        var folder = store.FolderOf(model);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException(Sentence($"humcp could not create {folder}: {ex.Message}"), ex);
        }

        var done = store.BytesOnDisk(model);
        CheckSpace(folder, model.TotalSize - done);
        progress?.Report(new ModelDownloadProgress(done, model.TotalSize));

        foreach (var file in model.Files)
        {
            cancellation.ThrowIfCancellationRequested();

            var path = store.PathOf(model, file);
            if (File.Exists(path) && new FileInfo(path).Length == file.Size) continue;

            var before = done;
            await FetchAsync(model, file, path, bytes => progress?.Report(new ModelDownloadProgress(before + bytes, model.TotalSize)),
                cancellation).ConfigureAwait(false);
            done = before + file.Size;
        }

        progress?.Report(new ModelDownloadProgress(model.TotalSize, model.TotalSize));
    }

    private async Task FetchAsync(ModelManifest model, ModelFile file, string path, Action<long> report, CancellationToken cancellation)
    {
        var partial = path + ModelStore.PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long have = 0;

        try
        {
            // Carrying on: what is already here is hashed first, so the check at the end still covers
            // every byte — including ones that arrived in an earlier run of humcp.
            if (File.Exists(partial))
            {
                have = new FileInfo(partial).Length;
                if (have > file.Size)
                {
                    File.Delete(partial);
                    have = 0;
                }
                else
                {
                    await HashExistingAsync(partial, hash, cancellation).ConfigureAwait(false);
                }
            }

            if (have < file.Size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, model.UrlOf(file));
                if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false);

                if (have > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    // The server sent the whole file rather than the rest of it: start this one again.
                    have = 0;
                    hash.GetHashAndReset();
                }
                else if (!response.IsSuccessStatusCode)
                {
                    throw new ModelDownloadException(Sentence(
                        $"The download of {file.Path} was refused: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                        "Downloading again carries on from where it stopped."));
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var target = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
                    FileShare.None, BufferSize, useAsync: true);

                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    if (have + read > file.Size)
                        throw new ModelDownloadException(Sentence($"{file.Path} arrived larger than it should be, so it was not kept."));

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    have += read;
                    report(have);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellation.IsCancellationRequested)
        {
            throw new ModelDownloadException(Sentence(
                $"The download stopped: {ex.Message} Downloading again carries on from where it stopped."), ex);
        }

        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (have != file.Size || !string.Equals(digest, file.Sha256, StringComparison.Ordinal))
        {
            TryDelete(partial);
            throw new ModelDownloadException(Sentence(have != file.Size
                ? $"{file.Path} ended early, at {have:N0} of {file.Size:N0} bytes. Downloading again carries on from where it stopped."
                : $"{file.Path} did not match its checksum, so it was deleted rather than used. Downloading again fetches it afresh."));
        }

        try
        {
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException(Sentence($"{file.Path} arrived but could not be put in place: {ex.Message}"), ex);
        }
    }

    private static async Task HashExistingAsync(string path, IncrementalHash hash, CancellationToken cancellation)
    {
        await using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await existing.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    private static void CheckSpace(string folder, long needed)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return; // Not knowing is not a reason to refuse; the write will say if the disk fills.
        }

        if (free < needed + DiskHeadroom)
            throw new ModelDownloadException(Sentence(
                $"There is not enough room on the disk: the model needs {Describe(needed)} more, and {Describe(free)} is free."));
    }

    /// <summary>A size as it is read aloud: "2.8 gigabytes", "740 megabytes".</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} gigabytes",
        >= 1_000_000 => $"{bytes / 1e6:0} megabytes",
        _ => $"{Math.Max(bytes, 0) / 1e3:0} kilobytes",
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the next attempt to replace */ }
    }

    private static string Sentence(string text) => TextSanitizer.Clean(text, TextSanitizer.HelpLength);
}

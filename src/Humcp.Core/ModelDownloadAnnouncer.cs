namespace Humcp.Core;

/// <summary>
/// Decides which download progress reports are worth saying. A 2.9 GB download reports thousands
/// of times; the status line is a live region, and every change to it is spoken.
/// </summary>
/// <remarks>
/// A line every tenth of the way, and never two within <see cref="Interval"/> — so a fast
/// connection does not say "10 percent, 20 percent" back to back, and a slow one is still heard
/// from every few minutes rather than going silent. The same idea as a tool's progress
/// (<see cref="ProgressThrottle"/>), measured in bytes rather than in a server's own steps.
/// </remarks>
public sealed class ModelDownloadAnnouncer(Func<DateTime>? clock = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private int _lastTenth = -1;
    private DateTime _lastSaid = DateTime.MinValue;

    /// <summary>The line to say for this report, or null to stay quiet.</summary>
    public string? Offer(ModelDownloadProgress progress)
    {
        var tenth = progress.Percent / 10;
        if (tenth <= _lastTenth || progress.Done >= progress.Total) return null;

        var now = _now();

        // The first report is said at once, whatever it is: it is the answer to "Download", and it
        // says whether this is a fresh start or carrying on.
        if (_lastTenth >= 0 && now - _lastSaid < Interval) return null;

        _lastTenth = tenth;
        _lastSaid = now;

        return $"Downloading the model: {progress.Percent} percent, {Gigabytes(progress.Done)} of {Gigabytes(progress.Total)} gigabytes.";
    }

    private static string Gigabytes(long bytes) => (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
}

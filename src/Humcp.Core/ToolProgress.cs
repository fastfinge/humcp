using System.Globalization;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// One <c>notifications/progress</c> from a running call, neutral and already sanitised: the
/// message is a third party's text on its way to a speech synthesiser like every other (§4,
/// pitfall 5).
/// </summary>
public sealed record ToolProgress(double Progress, double? Total, string Message);

/// <summary>
/// Decides which progress reports are said aloud (ROADMAP §4, pitfall 9): at most one every
/// <see cref="Interval"/>, none in the first interval after the call started, and none at all once
/// it has finished.
/// </summary>
/// <remarks>
/// <para>
/// The general case of the policy Phase 5 found it needed. A completion said a moment after
/// "still running" is a completion nobody hears, because the second announcement interrupts the
/// first (phase5-notes 4.4) — and progress is the same collision, repeatedly. Silence for the first
/// interval means a call that takes a second says "Running" and then its result, and nothing in
/// between to tread on either.
/// </para>
/// <para>
/// Over the limit is dropped, not queued. Progress that is two seconds old is not progress.
/// </para>
/// </remarks>
public sealed class ProgressThrottle
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _quietUntil;
    private bool _finished;

    /// <summary>Starts the throttle when the call does.</summary>
    public ProgressThrottle(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _quietUntil = _clock() + Interval;
    }

    /// <summary>The sentence to say for this report now, or null to say nothing.</summary>
    public string? Offer(string tool, ToolProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (_finished) return null;

        var now = _clock();
        if (now < _quietUntil) return null;

        _quietUntil = now + Interval;
        return Describe(tool, progress);
    }

    /// <summary>The call has an answer; nothing after this is news.</summary>
    public void Finish() => _finished = true;

    /// <summary>
    /// "Search documents, 40 percent. Reading the index." A percentage only when the server stated a
    /// total, because 40 of an unknown number is not a proportion of anything.
    /// </summary>
    public static string Describe(string tool, ToolProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var name = TextSanitizer.Clean(tool, TextSanitizer.NameLength);
        var message = TextSanitizer.Clean(progress.Message, TextSanitizer.HelpLength);

        var amount = progress.Total is > 0 and var total
            ? $"{Math.Clamp((int)Math.Round(100 * progress.Progress / total), 0, 100)} percent"
            : message.Length > 0 ? "" : $"step {progress.Progress.ToString("0.##", CultureInfo.CurrentCulture)}";

        var head = amount.Length > 0 ? $"{name}, {amount}." : $"{name}:";
        return message.Length > 0 ? $"{head} {message}" : head;
    }
}

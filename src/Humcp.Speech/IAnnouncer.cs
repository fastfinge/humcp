namespace Humcp.Speech;

/// <summary>
/// The seam ROADMAP §4 pitfall 7 asks for: prism behind our own interface, with a no-op
/// implementation so the app is completely usable if the native library fails to load.
/// </summary>
/// <remarks>
/// <para>
/// <b>UIA does the nouns, prism does the verbs</b> (ROADMAP §7). The screen reader reads controls
/// through UI Automation on its own. An announcer is for what UI Automation cannot know: a call
/// completing, a connection dropping, an error count, a pane change. Speaking control contents
/// here makes every user hear everything twice, and is the classic way to misuse a library like
/// this one.
/// </para>
/// </remarks>
public interface IAnnouncer : IDisposable
{
    /// <summary>False when running on the no-op implementation.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable account of which backend was chosen and what it supports.</summary>
    string Diagnostics { get; }

    /// <summary>
    /// Raised when a backend appears or disappears — a screen reader started or stopped mid-session.
    /// </summary>
    /// <remarks>
    /// Delivered on a background thread (phase0-findings §4). A UI subscriber must marshal to its
    /// own thread before touching anything.
    /// </remarks>
    event Action<string>? AvailabilityChanged;

    /// <summary>Speak. Verbs only.</summary>
    void Announce(string text, bool interrupt = false);

    /// <summary>Speech and braille together.</summary>
    void Output(string text, bool interrupt = false);

    /// <summary>Braille only.</summary>
    void Braille(string text);

    /// <summary>
    /// Rate-limited channel for progress chatter: at most one utterance per two seconds
    /// (ROADMAP §4, pitfall 9). Anything over the limit is dropped, not queued — stale progress is
    /// worse than none.
    /// </summary>
    void AnnounceProgress(string text);

    /// <summary>Stop whatever is being spoken.</summary>
    void Stop();
}

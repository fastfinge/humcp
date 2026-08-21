namespace Humcp.Speech;

/// <summary>
/// The fallback that makes prism optional. Every method does nothing; nothing throws. If this is
/// what the app is running on, the only thing the user loses is the supplemental announcements —
/// the whole UI is still readable through UI Automation.
/// </summary>
public sealed class NullAnnouncer(string reason) : IAnnouncer
{
    public bool IsAvailable => false;

    public string Diagnostics { get; } = "Speech is unavailable. " + reason;

#pragma warning disable CS0067 // Never raised: with no backend there are no transitions to report.
    public event Action<string>? AvailabilityChanged;
#pragma warning restore CS0067

    public void Announce(string text, bool interrupt = false) { }
    public void Output(string text, bool interrupt = false) { }
    public void Braille(string text) { }
    public void AnnounceProgress(string text) { }
    public void Stop() { }
    public void Dispose() { }
}

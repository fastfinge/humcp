using System.Diagnostics;
using System.Text;
using Prismatoid;

namespace Humcp.Spike;

/// <summary>
/// The seam ROADMAP §4 pitfall 7 asks for: prism behind our own interface, with a no-op
/// implementation so the app is fully functional if the native library fails to load.
/// </summary>
public interface IAnnouncer : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Human-readable description of what backend we ended up on, for the diagnostics pane.</summary>
    string Diagnostics { get; }

    /// <summary>Speak. Verbs only — never control contents (ROADMAP §7).</summary>
    void Announce(string text, bool interrupt = false);

    /// <summary>Speech and braille together.</summary>
    void Output(string text, bool interrupt = false);

    void Braille(string text);

    /// <summary>Rate-limited channel for progress chatter (pitfall 9: no more than one per ~2s).</summary>
    void AnnounceProgress(string text);

    void Stop();
}

public sealed class NullAnnouncer : IAnnouncer
{
    public bool IsAvailable => false;
    public string Diagnostics { get; }

    public NullAnnouncer(string reason) => Diagnostics = "No-op announcer. " + reason;

    public void Announce(string text, bool interrupt = false) { }
    public void Output(string text, bool interrupt = false) { }
    public void Braille(string text) { }
    public void AnnounceProgress(string text) { }
    public void Stop() { }
    public void Dispose() { }
}

public sealed class PrismAnnouncer : IAnnouncer
{
    private readonly PrismContext _context;
    private readonly IPrismBackend _backend;
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private readonly object _gate = new();
    private bool _firstProgress = true;

    public bool IsAvailable => true;
    public string Diagnostics { get; private set; }

    /// <summary>Raised when a backend appears or disappears (a screen reader starting mid-session).</summary>
    public event Action<string>? AvailabilityChanged;

    private PrismAnnouncer(PrismContext context, IPrismBackend backend, string diagnostics)
    {
        _context = context;
        _backend = backend;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Never throws. Returns a <see cref="NullAnnouncer"/> if prism cannot be initialised, which is
    /// the whole point of the wrapper.
    /// </summary>
    public static IAnnouncer Create(Action<string>? log = null)
    {
        PrismContext? context = null;
        try
        {
            PrismLog.SetLevel(PrismLogLevel.Info);
            PrismLog.SetHandler((level, source, message) => log?.Invoke($"[prism {level}] {source}: {message}"));

            PrismAnnouncer? created = null;
            context = new PrismContext(availabilityChanged: (id, name, available) =>
                created?.AvailabilityChanged?.Invoke($"{name} is now {(available ? "available" : "unavailable")}"));

            var report = DescribeRegistry(context);
            var backend = context.AcquireBestBackend();

            var diagnostics = new StringBuilder()
                .AppendLine($"prism backend in use: {backend.Name}")
                .AppendLine($"  speak: {backend.Features.HasFlag(PrismBackendFeature.SupportsSpeak)}" +
                            $"   braille: {backend.Features.HasFlag(PrismBackendFeature.SupportsBraille)}" +
                            $"   output: {backend.Features.HasFlag(PrismBackendFeature.SupportsOutput)}")
                .AppendLine($"  live at runtime: {backend.Features.HasFlag(PrismBackendFeature.IsSupportedAtRuntime)}")
                .AppendLine($"  auto power management supported: {PrismContext.IsAutoPowerManagementSupported()}")
                .AppendLine()
                .Append(report)
                .ToString();

            created = new PrismAnnouncer(context, backend, diagnostics);
            return created;
        }
        catch (Exception ex)
        {
            context?.Dispose();
            log?.Invoke("prism initialisation failed: " + ex);
            return new NullAnnouncer($"prism initialisation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string DescribeRegistry(PrismContext context)
    {
        var sb = new StringBuilder("Registered backends, descending priority:").AppendLine();
        foreach (var info in context.AvailableBackends)
        {
            var live = "";
            try
            {
                using var probe = context.AcquireBackend(info.Id);
                live = probe.Features.HasFlag(PrismBackendFeature.IsSupportedAtRuntime) ? "  RUNNING" : "";
            }
            catch (PrismException ex)
            {
                live = $"  (unavailable: {ex.Error})";
            }

            var known = info.Id == PrismBackendId.Nvda ? " [NVDA]"
                      : info.Id == PrismBackendId.Jaws ? " [JAWS]"
                      : info.Id == PrismBackendId.Sapi ? " [SAPI]"
                      : info.Id == PrismBackendId.OneCore ? " [OneCore]"
                      : info.Id == PrismBackendId.Uia ? " [UIA]"
                      : "";
            sb.AppendLine($"  {info.Name}{known} (priority {info.Priority}){live}");
        }
        return sb.ToString();
    }

    public void Announce(string text, bool interrupt = false)
        => Guard(() => _backend.Speak(Sanitize(text), interrupt));

    public void Output(string text, bool interrupt = false)
        => Guard(() => _backend.Output(Sanitize(text), interrupt));

    public void Braille(string text)
        => Guard(() => _backend.Braille(Sanitize(text)));

    public void AnnounceProgress(string text)
    {
        lock (_gate)
        {
            if (!_firstProgress && _sinceProgress.ElapsedMilliseconds < 2000) return;
            _firstProgress = false;
            _sinceProgress.Restart();
        }
        Announce(text);
    }

    public void Stop() => Guard(() => _backend.Stop());

    /// <summary>
    /// All server-supplied text is untrusted and heading for a synthesiser (ROADMAP §4 pitfall 5).
    /// The real implementation lives in Humcp.Core; this is the minimum for the spike.
    /// </summary>
    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var c = char.IsControl(ch) ? ' ' : ch;
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else lastWasSpace = false;
            sb.Append(c);
            if (sb.Length >= 1000) break;
        }
        return sb.ToString().Trim();
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (PrismException ex) { Debug.WriteLine($"prism call failed: {ex.Error} {ex.Message}"); }
        catch (Exception ex) { Debug.WriteLine("prism call failed: " + ex); }
    }

    public void Dispose()
    {
        try { _backend.Dispose(); } catch { /* throwaway spike */ }
        try { _context.Dispose(); } catch { }
    }
}

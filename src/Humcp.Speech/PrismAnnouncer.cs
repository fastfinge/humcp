using System.Diagnostics;
using System.Text;
using Humcp.Schema;
using Prismatoid;

namespace Humcp.Speech;

/// <summary>
/// <see cref="IAnnouncer"/> over prismatoid 0.4.0. Every call into the native library is guarded,
/// because the package is very new (ROADMAP §4, pitfall 7) and a speech library failing must never
/// be the reason a tool call does not happen.
/// </summary>
public sealed class PrismAnnouncer : IAnnouncer
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    private readonly PrismContext _context;
    private readonly IPrismBackend _backend;
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private bool _spokenProgress;
    private bool _disposed;

    private PrismAnnouncer(PrismContext context, IPrismBackend backend, string diagnostics)
    {
        _context = context;
        _backend = backend;
        Diagnostics = diagnostics;
    }

    public bool IsAvailable => true;

    public string Diagnostics { get; }

    /// <inheritdoc />
    /// <remarks>prism raises these on a background thread; subscribers must marshal.</remarks>
    public event Action<string>? AvailabilityChanged;

    /// <summary>
    /// Never throws. Returns a <see cref="NullAnnouncer"/> when prism cannot be initialised, which
    /// is the entire reason this wrapper exists.
    /// </summary>
    /// <param name="log">Receives prism's own log lines, for the diagnostics window.</param>
    public static IAnnouncer Create(Action<string>? log = null)
    {
        PrismContext? context = null;

        try
        {
            PrismLog.SetLevel(PrismLogLevel.Info);
            PrismLog.SetHandler((level, source, message) => log?.Invoke($"[prism {level}] {source}: {message}"));

            PrismAnnouncer? created = null;
            context = new PrismContext(availabilityChanged: (_, name, available) =>
                created?.AvailabilityChanged?.Invoke(
                    $"{TextSanitizer.Clean(name, 60)} is now {(available ? "available" : "unavailable")}"));

            var registry = DescribeRegistry(context);
            var backend = context.AcquireBestBackend();

            created = new PrismAnnouncer(context, backend, Describe(backend, registry));
            return created;
        }
        catch (Exception ex)
        {
            context?.Dispose();
            log?.Invoke("prism initialisation failed: " + ex);
            return new NullAnnouncer($"prism initialisation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Announce(string text, bool interrupt = false)
        => Guard(() => _backend.Speak(TextSanitizer.CleanForSpeech(text), interrupt));

    public void Output(string text, bool interrupt = false)
        => Guard(() => _backend.Output(TextSanitizer.CleanForSpeech(text), interrupt));

    public void Braille(string text)
        => Guard(() => _backend.Braille(TextSanitizer.CleanForSpeech(text)));

    public void AnnounceProgress(string text)
    {
        lock (_gate)
        {
            if (_spokenProgress && _sinceProgress.Elapsed < ProgressInterval) return;
            _spokenProgress = true;
            _sinceProgress.Restart();
        }

        Announce(text);
    }

    public void Stop() => Guard(_backend.Stop);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Guard(_backend.Dispose);
        try { _context.Dispose(); }
        catch (Exception ex) { Debug.WriteLine("prism context dispose failed: " + ex); }
    }

    private void Guard(Action action)
    {
        if (_disposed) return;

        try
        {
            action();
        }
        catch (PrismException ex)
        {
            Debug.WriteLine($"prism call failed: {ex.Error} {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("prism call failed: " + ex);
        }
    }

    private static string Describe(IPrismBackend backend, string registry) => new StringBuilder()
        .AppendLine($"prism backend in use: {backend.Name}")
        .AppendLine($"  speak: {backend.Features.HasFlag(PrismBackendFeature.SupportsSpeak)}" +
                    $"   braille: {backend.Features.HasFlag(PrismBackendFeature.SupportsBraille)}" +
                    $"   output: {backend.Features.HasFlag(PrismBackendFeature.SupportsOutput)}")
        .AppendLine($"  live at runtime: {backend.Features.HasFlag(PrismBackendFeature.IsSupportedAtRuntime)}")
        .AppendLine($"  auto power management supported: {PrismContext.IsAutoPowerManagementSupported()}")
        .AppendLine()
        .Append(registry)
        .ToString();

    /// <summary>
    /// Enumerates the backend registry. Note that a screen reader backend exposes no voices at all
    /// (phase0-findings §4) — settings UI must not assume a voice list exists, because with NVDA
    /// the rate and voice belong to the screen reader, not to us.
    /// </summary>
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

            sb.AppendLine($"  {info.Name} (priority {info.Priority}){live}");
        }

        return sb.ToString();
    }
}

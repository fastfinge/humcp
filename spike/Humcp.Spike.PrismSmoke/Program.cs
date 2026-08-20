using System.Diagnostics;
using Prismatoid;

namespace Humcp.Spike.PrismSmoke;

/// <summary>
/// ROADMAP §10, Phase 0: "smoke-test prismatoid — confirm it loads, speaks, and detects a running
/// screen reader." Pitfall 7 says the package is brand new, so the interesting outcomes are the
/// failure modes, not the happy path.
///
///   --quiet    enumerate only; do not make any sound
///   --braille  also send a braille-only line
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var quiet = args.Contains("--quiet");
        var braille = args.Contains("--braille");

        Console.WriteLine($"prismatoid smoke test  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine(new string('=', 72));

        PrismLog.SetLevel(PrismLogLevel.Debug);
        PrismLog.SetHandler((level, source, message) =>
            Console.WriteLine($"  [log {level,-5}] {source}: {message}"));

        PrismContext context;
        var sw = Stopwatch.StartNew();
        try
        {
            context = new PrismContext(availabilityChanged: (_, name, available) =>
                Console.WriteLine($"  [availability] {name} -> {(available ? "available" : "unavailable")}"));
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("FAIL: PrismContext could not be constructed.");
            Console.WriteLine("      " + ex);
            Console.WriteLine();
            Console.WriteLine("Consequence: humcp runs on the no-op announcer. The app must stay fully");
            Console.WriteLine("usable in that state (pitfall 7). Verify that before shipping.");
            return 1;
        }

        using (context)
        {
            Console.WriteLine($"Context initialised in {sw.ElapsedMilliseconds} ms.");
            Console.WriteLine($"Automatic power management supported: {PrismContext.IsAutoPowerManagementSupported()}");
            Console.WriteLine();

            Console.WriteLine("Registered backends, descending priority:");
            var running = new List<string>();
            foreach (var info in context.AvailableBackends)
            {
                string state;
                var features = default(PrismBackendFeature);
                try
                {
                    using var probe = context.AcquireBackend(info.Id);
                    features = probe.Features;
                    var live = features.HasFlag(PrismBackendFeature.IsSupportedAtRuntime);
                    state = live ? "RUNNING" : "not running";
                    if (live) running.Add(info.Name);
                }
                catch (PrismException ex)
                {
                    state = "unavailable (" + ex.Error + ")";
                }

                Console.WriteLine($"  {info.Name,-24} priority {info.Priority,4}   {state}");
                if (features != default)
                {
                    Console.WriteLine($"      speak={features.HasFlag(PrismBackendFeature.SupportsSpeak)}" +
                                      $"  braille={features.HasFlag(PrismBackendFeature.SupportsBraille)}" +
                                      $"  output={features.HasFlag(PrismBackendFeature.SupportsOutput)}" +
                                      $"  stop={features.HasFlag(PrismBackendFeature.SupportsStop)}" +
                                      $"  ssml={features.HasFlag(PrismBackendFeature.SupportsSpeakSsml)}");
                }
            }

            Console.WriteLine();
            var screenReaders = new (PrismBackendId Id, string Name)[]
            {
                (PrismBackendId.Nvda, "NVDA"),
                (PrismBackendId.Jaws, "JAWS"),
                (PrismBackendId.Uia, "Narrator / UIA"),
                (PrismBackendId.SystemAccess, "System Access"),
                (PrismBackendId.ZoomText, "ZoomText"),
            };

            Console.WriteLine("Screen reader detection:");
            var detected = false;
            foreach (var (id, name) in screenReaders)
            {
                bool live;
                try
                {
                    using var probe = context.AcquireBackend(id);
                    live = probe.Features.HasFlag(PrismBackendFeature.IsSupportedAtRuntime);
                }
                catch (PrismException) { live = false; }

                Console.WriteLine($"  {name,-18} {(live ? "DETECTED" : "-")}");
                detected |= live;
            }

            if (!detected)
                Console.WriteLine("  (none — run this again with NVDA or JAWS active to test the detection path)");

            Console.WriteLine();
            IPrismBackend backend;
            try
            {
                backend = context.AcquireBestBackend();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: no backend could be acquired. " + ex.Message);
                return 1;
            }

            using (backend)
            {
                Console.WriteLine($"Best backend: {backend.Name}");
                try
                {
                    var voices = backend.Voices.Take(5).ToList();
                    Console.WriteLine($"Voices (first {voices.Count}): " +
                                      string.Join(", ", voices.Select(v => $"{v.Name} [{v.Language}]")));
                    Console.WriteLine($"Current voice: {backend.CurrentVoice?.Name ?? "(none)"}");
                }
                catch (PrismException ex)
                {
                    Console.WriteLine($"Voices unavailable: {ex.Error}");
                }

                if (quiet)
                {
                    Console.WriteLine();
                    Console.WriteLine("--quiet: skipping speech.");
                    return 0;
                }

                Console.WriteLine();
                Console.WriteLine("Speaking a test phrase…");
                try
                {
                    sw.Restart();
                    backend.Speak("humcp phase zero. Tool call complete, 3 results.", interrupt: true);
                    Console.WriteLine($"  Speak() returned in {sw.ElapsedMilliseconds} ms " +
                                      "(returning fast means it is asynchronous, which is what the UI thread needs).");
                    Thread.Sleep(300);
                    Console.WriteLine($"  IsSpeaking: {SafeIsSpeaking(backend)}");
                }
                catch (PrismException ex)
                {
                    Console.WriteLine($"FAIL: Speak() threw {ex.Error}: {ex.Message}");
                    return 1;
                }

                if (braille)
                {
                    try
                    {
                        backend.Braille("humcp phase zero braille line.");
                        Console.WriteLine("  Braille() accepted.");
                    }
                    catch (PrismException ex)
                    {
                        Console.WriteLine($"  Braille() threw {ex.Error} — expected on a backend without braille.");
                    }
                }

                Thread.Sleep(2500);
                try { backend.Stop(); } catch (PrismException) { }
            }
        }

        Console.WriteLine();
        Console.WriteLine("PASS: prismatoid loaded, enumerated backends and spoke without throwing.");
        Console.WriteLine("Did you actually hear it? That is the part this program cannot check.");
        return 0;
    }

    private static string SafeIsSpeaking(IPrismBackend backend)
    {
        try { return backend.IsSpeaking.ToString(); }
        catch (PrismException ex) { return "unsupported (" + ex.Error + ")"; }
    }
}

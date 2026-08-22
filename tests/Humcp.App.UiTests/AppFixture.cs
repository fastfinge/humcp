using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Humcp.App.UiTests;

/// <summary>
/// Launches the real application once and hands the suite its live UIA tree. The app connects to
/// the fixture server on startup, so "ready" means the tree has tools in it.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private Application? _app;

    public UIA3Automation Automation { get; } = new();

    public Window Window { get; private set; } = null!;

    /// <summary>
    /// A window the app opened beside its main one — a generated form, or the sub-form an object
    /// list opens for a row (§8: windows are non-modal, and several can be open at once).
    /// </summary>
    /// <remarks>
    /// Looked for among the main window's children rather than among the process's top-level
    /// windows. An owned WPF window has its own top-level handle but UI Automation reports it under
    /// its owner, and <c>GetAllTopLevelWindows</c> does not return it at all — which reads as "the
    /// form never opened" when the form is open and announcing itself.
    /// </remarks>
    public Window? Owned(string title, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));

        do
        {
            var match = OpenWindows().FirstOrDefault(w => UiaTree.Safe(() => w.Title) == title);

            if (match is not null) return match;
            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>
    /// Descendants rather than children: a sub-form opened from a generated form is owned by that
    /// form, so it sits two levels down.
    /// </summary>
    private IEnumerable<Window> OpenWindows() =>
        Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
            .Select(w => w.AsWindow())
            .Concat(_app?.GetAllTopLevelWindows(Automation) ?? []);

    /// <summary>Every window the application currently has open, for a failure message worth reading.</summary>
    public IReadOnlyList<string> Titles() =>
        [.. OpenWindows().Select(w => UiaTree.Safe(() => w.Title) ?? "(none)")];

    public bool HasExited => _app?.HasExited ?? true;

    /// <summary>Where the app under test keeps its parameter history. Never the tester's own profile.</summary>
    public string HistoryPath { get; } = Path.Combine(
        Path.GetTempPath(), "humcp-uitests", Guid.NewGuid().ToString("n"), "history.json");

    public ValueTask InitializeAsync()
    {
        var executable = LocateApplication();

        // Inherited by the child process. The suite runs real tools from real forms, and MRU
        // history is written to disk on a successful call (§4, pitfall 3) — into a temporary
        // directory here, so a test run never leaves anything in the profile of whoever ran it.
        Environment.SetEnvironmentVariable("HUMCP_HISTORY", HistoryPath);

        _app = Application.Launch(executable);

        Window = _app.GetMainWindow(Automation, TimeSpan.FromSeconds(30))
                 ?? throw new InvalidOperationException("humcp did not open a window.");

        WaitForTools();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _app?.Close(); } catch { /* best effort */ }
        try { if (_app is { HasExited: false }) _app.Kill(); } catch { /* best effort */ }

        Automation.Dispose();
        _app?.Dispose();

        try
        {
            var directory = Path.GetDirectoryName(HistoryPath);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test run is not a failure worth reporting.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Waits until the server has connected and its tools are in the tree. Starting a child
    /// process and listing its tools is the slowest thing the app does.
    /// </summary>
    private void WaitForTools()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);

        while (DateTime.UtcNow < deadline)
        {
            var items = Window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));
            if (items.Length > 1) return;
            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            "The tree never filled with tools. Status line says: " +
            (Window.FindFirstDescendant(cf => cf.ByAutomationId("LiveStatus"))?.Name ?? "(no status)"));
    }

    private static string LocateApplication()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Humcp.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";

        var executable = Path.Combine(
            directory.FullName, "src", "Humcp.App", "bin", configuration, "net10.0-windows", "Humcp.App.exe");

        if (!File.Exists(executable))
            throw new InvalidOperationException($"humcp is not built: {executable} does not exist.");

        return executable;
    }
}

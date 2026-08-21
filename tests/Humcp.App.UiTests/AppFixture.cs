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

    public ValueTask InitializeAsync()
    {
        var executable = LocateApplication();
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

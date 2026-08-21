using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// Synthetic keyboard input, with the guard the Phase 0 probe learned the hard way: never send a
/// keystroke unless the application under test genuinely owns the foreground, or the test types
/// into whatever the person at this machine is doing (ROADMAP §9).
/// </summary>
internal static class Input
{
    public static void Settle(int milliseconds = 60)
    {
        Wait.UntilInputIsProcessed();
        Thread.Sleep(milliseconds);
    }

    /// <summary>
    /// Takes the foreground, and refuses to continue if it cannot. Retried rather than attempted
    /// once: Windows declines <c>SetForegroundWindow</c> outright when the calling process is not
    /// already in front, which happens whenever this suite runs alongside the other test projects.
    /// One attempt made every keyboard-driven test in this file fail together, in three seconds,
    /// on a busy machine.
    /// </summary>
    public static void EnsureForeground(Window window, UIA3Automation automation)
    {
        var processId = window.Properties.ProcessId.Value;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                window.SetForeground();
                window.Focus();
            }
            catch (Exception)
            {
                // A refused grab is a transient state, not a broken window: try again.
            }

            Settle(250);

            if (automation.FocusedElement()?.Properties.ProcessId.ValueOrDefault == processId) return;
        }

        Assert.Fail(
            "humcp did not take the foreground within ten seconds, so no keystrokes were sent. " +
            "Something else on this desktop is holding focus; re-run when the machine is free.");
    }

    public static void Press(VirtualKeyShort key)
    {
        Keyboard.Press(key);
        Keyboard.Release(key);
        Settle();
    }

    /// <summary>
    /// Tabs around the window and reports every distinct stop as <c>ControlType:Name</c>, stopping
    /// when it returns to where it started. Fails rather than continuing if focus ever leaves the
    /// application.
    /// </summary>
    public static List<string> WalkTabOrder(Window window, UIA3Automation automation, out bool cycled)
    {
        var processId = window.Properties.ProcessId.Value;
        var stops = new List<string>();
        var seen = new HashSet<string>();
        string? first = null;

        cycled = false;

        for (var i = 0; i < 40; i++)
        {
            Press(VirtualKeyShort.TAB);

            var focused = automation.FocusedElement();
            if (focused is null) continue;

            if (focused.Properties.ProcessId.ValueOrDefault != processId)
            {
                Assert.Fail("Focus left humcp during the tab walk; stopping before keystrokes leak elsewhere.");
            }

            var stop = $"{UiaTree.Safe(() => focused.ControlType)}:{UiaTree.Flatten(UiaTree.Safe(() => focused.Name) ?? "", 40)}";
            first ??= stop;

            if (i > 0 && stop == first)
            {
                cycled = true;
                break;
            }

            if (seen.Add(stop)) stops.Add(stop);
        }

        return stops;
    }
}

using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Humcp.App;

/// <summary>
/// Every status line in humcp says what it says through here: the text is set, the live-region event
/// raised, and the line remembered so <kbd>Ctrl+R</kbd> can say it again.
/// </summary>
/// <remarks>
/// <para>
/// A polite live region is spoken once, when its text changes, and only while its window has the
/// foreground. Missed — because NVDA was speaking something else, or because another program was in
/// front — it was gone: NVDA's own "read status bar" cannot find a WPF status bar (phase9-notes 5.1),
/// and a status line inside a form is no status bar at all. The developer's answer is a key that
/// repeats it through prism, in every window.
/// </para>
/// <para>
/// One line for the whole application, not one per window: what was missed is whatever was said
/// last, wherever it was said.
/// </para>
/// </remarks>
internal static class LiveRegion
{
    /// <summary>The last thing any of humcp's status lines said, or null before anything has been.</summary>
    public static string? Last { get; private set; }

    /// <summary>Puts <paramref name="text"/> in the region, which a screen reader then speaks.</summary>
    /// <remarks>
    /// There is no quiet version: a reader announces a live region when its text changes, not when the
    /// event is raised (phase5-notes 4.8). A message that must not be spoken must not be put here.
    /// </remarks>
    public static void Say(TextBlock region, string text)
    {
        ArgumentNullException.ThrowIfNull(region);

        region.Text = text;
        if (!string.IsNullOrWhiteSpace(text)) Last = text;

        var peer = UIElementAutomationPeer.FromElement(region) ?? UIElementAutomationPeer.CreatePeerForElement(region);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

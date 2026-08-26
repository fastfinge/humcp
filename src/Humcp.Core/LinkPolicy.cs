namespace Humcp.Core;

/// <summary>What humcp will do with a link a server sent.</summary>
public enum LinkAction
{
    /// <summary>Nothing but copy it. The user is told why.</summary>
    None,

    /// <summary>Hand it to the default browser.</summary>
    Browse,

    /// <summary>Show the file in its folder, without opening it.</summary>
    Reveal,
}

/// <summary>
/// Which links humcp is prepared to hand to the shell, and what it does with the rest.
/// </summary>
/// <remarks>
/// <para>
/// A <c>resource_link</c> is a string chosen by a third party (ROADMAP §4, pitfall 5), and opening
/// a string with the shell is asking Windows to run whichever program claims its scheme. That is
/// the whole attack: <c>ms-something:</c>, a custom handler installed by anything on the machine,
/// or a <c>file:</c> URI ending in <c>.exe</c>. So the rule is an allow-list of two, and the third
/// case is deliberate rather than forgotten:
/// </para>
/// <list type="bullet">
/// <item><description><b>http and https</b> go to the browser, which is the one handler whose job
/// is to be handed untrusted addresses.</description></item>
/// <item><description><b>file</b> is <em>revealed</em>, not opened: the folder comes up with the
/// file selected, and what happens next is the user's decision, made with the name in front of
/// them.</description></item>
/// <item><description><b>Everything else</b> is copyable and inert, and says so — an edge that
/// explains itself rather than a button that does nothing (§7.18).</description></item>
/// </list>
/// </remarks>
public static class LinkPolicy
{
    public static LinkAction Action(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return LinkAction.None;

        if (parsed.Scheme is "http" or "https")
        {
            // A URI carrying credentials is one to look at rather than to follow: the browser would
            // strip them or warn about them, and this is not the place to find out which.
            return string.IsNullOrEmpty(parsed.UserInfo) ? LinkAction.Browse : LinkAction.None;
        }

        return parsed.IsFile ? LinkAction.Reveal : LinkAction.None;
    }

    /// <summary>What the button offers, and what it explains when there is no button.</summary>
    public static string Describe(string? uri) => Action(uri) switch
    {
        LinkAction.Browse => "Opens this address in your browser.",
        LinkAction.Reveal => "Shows this file in a folder window, without opening it.",
        _ => "humcp opens web addresses and files only, so this link can be copied but not opened.",
    };

    /// <summary>The label for the action, for a button that only exists when there is one.</summary>
    public static string ActionName(string? uri) => Action(uri) switch
    {
        LinkAction.Browse => "Open in browser",
        LinkAction.Reveal => "Show in folder",
        _ => "",
    };
}

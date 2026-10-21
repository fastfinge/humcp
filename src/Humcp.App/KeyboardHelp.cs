namespace Humcp.App;

/// <summary>
/// The keyboard map (ROADMAP §7), written out for Help, Keyboard shortcuts. Plain text in a
/// read-only box, so it is reviewed a line at a time like everything else humcp shows.
/// </summary>
internal static class KeyboardHelp
{
    public static string Text { get; } = string.Join(Environment.NewLine,
        "Everywhere",
        "  Alt                     Menu bar",
        "  F6, Shift+F6            Next and previous pane",
        "  Ctrl+1 to Ctrl+9        Switch to that window; the Window menu lists them all",
        "  Ctrl+W, Escape          Close the current window (not the main one)",
        "  F1                      Full details of the tool, field or result the caret is on",
        "  Ctrl+R                  Say the last announcement again",
        "",
        "The servers and tools tree",
        "  Ctrl+F                  Find a tool on every server; Escape clears the search",
        "  Enter                   Run the tool, or open its form if it has parameters",
        "  A letter                Move to the next item starting with it",
        "  Ctrl+N                  Add a server",
        "  Alt+Enter               Settings of the selected server",
        "  F5                      Reconnect the selected server",
        "  Delete, twice           Remove the selected server",
        "  Ctrl+Comma              humcp's settings",
        "  Shift+F10, Applications The context menu",
        "  Escape                  Stop a tool that is running",
        "",
        "A tool form",
        "  Ctrl+Enter              Run the tool",
        "  Ctrl+O                  Show or hide the optional parameters",
        "  Escape                  Stop the running tool; with nothing running, close the window",
        "",
        "A list of entries in a form, such as environment variables",
        "  Enter in the entry box  Add what is typed to the list",
        "  F2                      Change the selected entry, in a window of its own",
        "  Delete                  Remove the selected entry",
        "",
        "A result",
        "  Enter                   Open what the caret is on in a window of its own",
        "  Ctrl+Shift+C            Copy the value the caret is on",
        "  Ctrl+D                  Describe the picture the caret is on, with the local model",
        "  Shift+F10 in a table    Sort by the current column",
        "",
        "A question a server asks",
        "  Ctrl+Enter              Accept",
        "  Alt+D                   Decline",
        "  Escape                  Cancel",
        "",
        "Resizing the panes",
        "  View, Servers pane width   Set the split as a percentage");
}

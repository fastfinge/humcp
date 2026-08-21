namespace Humcp.Core;

/// <summary>
/// Everything needed to start talking to one server. Phase 1 has exactly one of these and it is
/// hard-coded (ROADMAP §10); Phase 6 reads a list of them from configuration, in a shape
/// deliberately close to the familiar <c>mcpServers</c> block (§8).
/// </summary>
/// <param name="Id">Stable key. Used for MRU history and settings later; never spoken.</param>
/// <param name="DisplayName">What the user hears on the server's tree node.</param>
/// <param name="Command">Executable to launch.</param>
/// <param name="Arguments">Arguments, already split — no shell involved.</param>
/// <param name="WorkingDirectory">Working directory for the child process, or null to inherit.</param>
public sealed record ServerDescriptor(
    string Id,
    string DisplayName,
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null)
{
    /// <summary>The command line as one string, for diagnostics panes and error messages.</summary>
    public string CommandLine =>
        Arguments.Count == 0 ? Command : Command + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string argument)
        => argument.Contains(' ') ? '"' + argument + '"' : argument;
}

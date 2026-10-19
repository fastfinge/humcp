namespace Humcp.Core;

/// <summary>
/// Everything needed to start talking to one server: one entry of <c>servers.json</c>, in a shape
/// deliberately close to the familiar <c>mcpServers</c> block so that an existing configuration can
/// be pasted in (§8). <see cref="ServerConfiguration"/> reads and writes these.
/// </summary>
/// <param name="Id">
/// The entry's key in <c>mcpServers</c>. Stable: MRU history is filed under it. Never spoken, because
/// <see cref="DisplayName"/> is what the tree says.
/// </param>
/// <param name="DisplayName">What the user hears on the server's tree node.</param>
/// <param name="Command">Executable to launch. Empty for a server reached by <see cref="Url"/>.</param>
/// <param name="Arguments">Arguments, already split — no shell involved.</param>
/// <param name="WorkingDirectory">Working directory for the child process, or null to inherit.</param>
public sealed record ServerDescriptor(
    string Id,
    string DisplayName,
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null)
{
    /// <summary>
    /// Variables set for the child process on top of humcp's own environment. Usually where an API
    /// key lives, so nothing in the app ever shows the values — only the names.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The address of a server reached over Streamable HTTP rather than started as a process
    /// (ROADMAP §10, Phase 7). Unauthenticated only: a server that wants a sign-in is v3's (§11).
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// False for an entry marked <c>"disabled": true</c>: it stays in the tree, says so, and is not
    /// started until it is enabled or reconnected by hand.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The command line as one string, for diagnostics panes and error messages.</summary>
    public string CommandLine =>
        Url is { Length: > 0 } url ? url
        : Arguments.Count == 0 ? Command
        : Command + " " + JoinArguments(Arguments);

    /// <summary>
    /// The arguments as one line, quoted where an argument has a space in it — the inverse of
    /// <see cref="SplitArguments"/>, and what the edit form puts in its Arguments box.
    /// </summary>
    public static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(Quote));

    /// <summary>
    /// Splits a command-line-ish string the way a shell would, honouring double quotes. The edit form
    /// takes arguments as one line, because a list with Add and Remove buttons is a great many key
    /// presses for <c>-y @modelcontextprotocol/server-everything</c>.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    started = true;
                    continue;
                case ' ' or '\t' when !inQuotes:
                    if (started) { arguments.Add(current.ToString()); current.Clear(); started = false; }
                    continue;
                default:
                    current.Append(c);
                    started = true;
                    continue;
            }
        }

        if (started) arguments.Add(current.ToString());
        return arguments;
    }

    private static string Quote(string argument)
        => argument.Length == 0 || argument.Contains(' ') || argument.Contains('\t')
            ? '"' + argument + '"'
            : argument;
}

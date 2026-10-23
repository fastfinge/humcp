using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// Every configured server and its connection, and the one place that changes both together: a
/// server added here is in the file and in the tree, and one removed is gone from both.
/// </summary>
/// <remarks>
/// A connection is kept for the life of its configuration entry and reconfigured in place when the
/// entry is edited, so a tool window holding it goes on working against the edited server instead
/// of holding a stale one. Identity is the object, not the id — an id can be renamed.
/// </remarks>
public sealed class ServerRegistry : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<ServerConnection> _connections = [];

    public ServerRegistry(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Configuration = configuration;

        foreach (var server in configuration.Servers) Attach(new ServerConnection(server));
    }

    public ServerConfiguration Configuration { get; }

    /// <summary>In configuration order, which is tree order.</summary>
    public IReadOnlyList<ServerConnection> Connections
    {
        get { lock (_gate) return [.. _connections]; }
    }

    /// <summary>A server's state, tools or failure changed. Raised on whatever thread changed it.</summary>
    public event EventHandler<ServerConnection>? ServerChanged;

    /// <summary>A server was added or removed.</summary>
    public event EventHandler? ServersChanged;

    public ServerConnection? Find(string id)
    {
        lock (_gate) return _connections.FirstOrDefault(c => c.Descriptor.Id == id);
    }

    public IReadOnlyList<ServerSnapshot> Snapshots() => [.. Connections.Select(c => c.Snapshot())];

    /// <summary>
    /// Starts every enabled server at once — one slow server must not hold up the rest — and returns
    /// when they have all either connected or failed.
    /// </summary>
    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        var starting = Connections.Where(c => c.Descriptor.Enabled).Select(c => c.ConnectAsync(cancellationToken));
        await Task.WhenAll(starting).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a server to the file and the tree, without starting it: a form that waited for an
    /// <c>npx</c> download before it closed would be a form that looked hung. Answers null when the
    /// file could not be written, because a server that would vanish at the next start is not one
    /// that has been added; <see cref="ServerConfiguration.LastError"/> says why.
    /// </summary>
    public ServerConnection? Add(ServerDescriptor server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (Configuration.Taken(server.Id))
            server = server with { Id = Configuration.UniqueId(server.Id) };

        Configuration.Put(server);

        if (!Configuration.Save())
        {
            Configuration.Remove(server.Id);
            return null;
        }

        var connection = new ServerConnection(server);
        Attach(connection);
        ServersChanged?.Invoke(this, EventArgs.Empty);
        return connection;
    }

    /// <summary>
    /// Replaces a server's configuration and stops it, ready to be started again by the caller.
    /// Answers false, and changes nothing, when the file could not be written.
    /// </summary>
    public async Task<bool> UpdateAsync(ServerConnection connection, ServerDescriptor server)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(server);

        var previous = connection.Descriptor;

        if (server.Id != previous.Id && Configuration.Taken(server.Id))
            server = server with { Id = Configuration.UniqueId(server.Id) };

        Configuration.Put(server, previous.Id);

        if (!Configuration.Save())
        {
            Configuration.Put(previous, server.Id);
            return false;
        }

        await connection.ReconfigureAsync(server).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Takes a server out of the file and the list at once, and stops its process in the background.
    /// Answers false, and keeps it, when the file could not be written.
    /// </summary>
    public bool Remove(ServerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var descriptor = connection.Descriptor;
        var index = Connections.ToList().IndexOf(connection);

        Configuration.Remove(descriptor.Id);

        if (!Configuration.Save())
        {
            Configuration.Put(descriptor);
            return false;
        }

        lock (_gate) _connections.Remove(connection);
        connection.Changed -= OnConnectionChanged;
        ServersChanged?.Invoke(this, EventArgs.Empty);

        // Not awaited. Stopping a child process can take its whole shutdown timeout, and found by
        // ear (phase6-notes): waiting for it left the server in the tree, and the user in silence,
        // for six seconds after the second Delete — long enough to press it a third time.
        _ = Task.Run(async () => await connection.DisposeAsync().ConfigureAwait(false));
        return index >= 0;
    }

    public async ValueTask DisposeAsync()
    {
        var connections = Connections;
        lock (_gate) _connections.Clear();

        foreach (var connection in connections)
        {
            connection.Changed -= OnConnectionChanged;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Attach(ServerConnection connection)
    {
        connection.Changed += OnConnectionChanged;
        lock (_gate) _connections.Add(connection);
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (sender is ServerConnection connection) ServerChanged?.Invoke(this, connection);
    }
}

/// <summary>
/// What the status line says about servers connecting. Pure, because every one of these is spoken,
/// and a sentence that can only be checked by launching the app is checked once.
/// </summary>
public static class ServerAnnouncements
{
    /// <summary>
    /// One sentence for a whole start-up, however many servers there are. Five servers each
    /// announcing themselves as they come up is five interruptions, and the last one wins.
    /// </summary>
    public static string Startup(IReadOnlyList<ServerSnapshot> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var enabled = servers.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) return "";

        if (enabled.Count == 1) return Connection(enabled[0]);

        var connected = enabled.Where(s => s.State == ServerConnectionState.Connected).ToList();
        var failed = enabled.Where(s => s.State == ServerConnectionState.Failed).ToList();
        var tools = connected.Sum(s => s.Tools.Count);
        var prompts = connected.Sum(s => s.Prompts.Count);

        // Prompts only when there are some, as on a server's own node. Found by ear: the one-server
        // sentence said them and this one did not (phase12-notes).
        var contents = prompts > 0
            ? $"{Count(tools, "tool", "tools")}, {Count(prompts, "prompt", "prompts")}"
            : Count(tools, "tool", "tools");

        var opening = connected.Count == enabled.Count
            ? $"Connected to {enabled.Count} servers, {contents}."
            : $"Connected to {connected.Count} of {enabled.Count} servers, {contents}.";

        return failed.Count == 0
            ? opening
            : $"{opening} Could not connect to {List(failed.Select(f => Name(f)))}.";
    }

    /// <summary>One server, after it has been started on its own.</summary>
    public static string Connection(ServerSnapshot server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.State switch
        {
            ServerConnectionState.Connected =>
                $"Connected to {Name(server)}. {Count(server.Tools.Count, "tool", "tools")}" +
                (server.Prompts.Count > 0 ? $", {Count(server.Prompts.Count, "prompt", "prompts")}" : "") +
                (string.IsNullOrWhiteSpace(server.NegotiatedProtocolVersion)
                    ? "."
                    : $", protocol {TextSanitizer.Clean(server.NegotiatedProtocolVersion, 40)}."),
            ServerConnectionState.Failed =>
                $"Could not connect to {Name(server)}. {TextSanitizer.Clean(server.FailureReason, TextSanitizer.HelpLength)}",
            ServerConnectionState.Connecting => $"Connecting to {Name(server)}.",
            _ when !server.Enabled => $"{Name(server)} is disabled.",
            _ => $"{Name(server)} is not connected.",
        };
    }

    /// <summary>
    /// What a server's tool list changing amounts to, or nothing when it amounts to nothing — a
    /// server that re-sends the same list has not said anything worth interrupting for.
    /// </summary>
    /// <remarks>
    /// Names are given up to three at a time. A server that swaps twenty tools at once is a server
    /// whose tree wants looking at, and twenty names read aloud is not a way to look at it.
    /// </remarks>
    /// <param name="noun">"tool", or "prompt" for the prompt list (Phase 12), which changes the same way.</param>
    public static string ToolsChanged(
        string server, IReadOnlyList<ToolInfo> before, IReadOnlyList<ToolInfo> after, string noun = "tool")
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var was = before.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var now = after.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var added = after.Where(t => !was.Contains(t.Name)).ToList();
        var removed = before.Where(t => !now.Contains(t.Name)).ToList();

        if (added.Count == 0 && removed.Count == 0) return "";

        static string Part(List<ToolInfo> tools, string verb) => tools.Count switch
        {
            0 => "",
            <= 3 => $"{tools.Count} {verb}, {List(tools.Select(t => t.DisplayName))}",
            _ => $"{tools.Count} {verb}",
        };

        var parts = new[] { Part(added, "added"), Part(removed, "removed") }.Where(p => p.Length > 0);

        return $"{TextSanitizer.Clean(server, TextSanitizer.NameLength)} changed its {noun}s: {string.Join("; ", parts)}. " +
               $"{Count(after.Count, noun, noun + "s")} now.";
    }

    /// <summary>
    /// A server that stopped without being asked to — crashed, killed, or an HTTP server gone away.
    /// Said unprompted, so it says what to do about it as well as what happened.
    /// </summary>
    public static string Stopped(ServerSnapshot server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return $"{Name(server)} stopped. {TextSanitizer.Clean(server.FailureReason, TextSanitizer.HelpLength)} F5 starts it again.";
    }

    /// <summary>
    /// What is said before running a tool that declares <c>destructiveHint</c> (ROADMAP §3), and how to
    /// run it anyway. The hint is the server's own claim and never proof either way (§4, pitfall 5), so
    /// the sentence says whose claim it is.
    /// </summary>
    public static string Destructive(ToolInfo tool, string keystroke)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return $"{TextSanitizer.Clean(tool.DisplayName, TextSanitizer.NameLength)} says it may delete or change things " +
               $"that cannot be put back. Press {keystroke} again to run it.";
    }

    private static string Name(ServerSnapshot server) => TextSanitizer.Clean(server.DisplayName, TextSanitizer.NameLength);

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        return all.Count switch
        {
            0 => "",
            1 => all[0],
            _ => string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1],
        };
    }

    private static string Count(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}

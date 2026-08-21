using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// Builds the tree of ROADMAP §8: server → Tools → optional prefix groups → tools. Pure, so the
/// grouping heuristic can be argued with in tests rather than by squinting at a running app.
/// </summary>
public static class ToolTreeBuilder
{
    /// <summary>
    /// Below this, grouping costs more than it saves: a user arrowing through six tools does not
    /// want two of the six presses spent on group headers.
    /// </summary>
    public const int MinimumToolsForGrouping = 8;

    /// <summary>A prefix earns a group at this many tools, not fewer.</summary>
    public const int MinimumGroupSize = 2;

    /// <summary>At least this many prefixes must qualify, or the split is not a pattern.</summary>
    public const int MinimumGroupCount = 2;

    public static CatalogueNode BuildServerNode(ServerSnapshot server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var children = new List<CatalogueNode>();

        // §8: category nodes appear only when non-empty. Prompts and Resources arrive in v2.
        if (server.Tools.Count > 0) children.Add(BuildToolsCategory(server.Tools));

        return new CatalogueNode(CatalogueNodeKind.Server, DescribeServer(server), children);
    }

    /// <summary>
    /// The server's accessible name, status folded in so it is heard on arrow-through rather than
    /// hidden in a pane the user has to go and find (§8).
    /// </summary>
    public static string DescribeServer(ServerSnapshot server)
    {
        var name = TextSanitizer.Clean(server.DisplayName, TextSanitizer.NameLength);

        return server.State switch
        {
            ServerConnectionState.Connecting => $"{name}, connecting",

            ServerConnectionState.Connected when server.Tools.Count == 0 =>
                $"{name}, connected, no tools{ProtocolSuffix(server)}",

            ServerConnectionState.Connected =>
                $"{name}, connected, {server.Tools.Count} tool{(server.Tools.Count == 1 ? "" : "s")}{ProtocolSuffix(server)}",

            ServerConnectionState.Failed =>
                $"{name}, error: {TextSanitizer.Clean(server.FailureReason, TextSanitizer.NameLength)}",

            _ => $"{name}, not connected",
        };
    }

    private static string ProtocolSuffix(ServerSnapshot server)
        => string.IsNullOrWhiteSpace(server.NegotiatedProtocolVersion)
            ? ""
            // §4, pitfall 8: which revision was negotiated is the first thing you want when a
            // server misbehaves, so it is on the node rather than in a log.
            : $", protocol {TextSanitizer.Clean(server.NegotiatedProtocolVersion, 40)}";

    private static CatalogueNode BuildToolsCategory(IReadOnlyList<ToolInfo> tools)
    {
        var children = Group(tools);
        var label = $"Tools, {tools.Count} item{(tools.Count == 1 ? "" : "s")}";
        return new CatalogueNode(CatalogueNodeKind.Category, label, children);
    }

    /// <summary>
    /// Splits tools into prefix groups when the names actually justify it. ROADMAP §13 records this
    /// heuristic as a guess to revisit against the harvested corpus; it is deliberately conservative
    /// so that a server which does not namespace consistently gets a flat list instead of noise.
    /// </summary>
    public static IReadOnlyList<CatalogueNode> Group(IReadOnlyList<ToolInfo> tools)
    {
        var byPrefix = tools
            .GroupBy(PrefixOf, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var qualifying = byPrefix
            .Where(g => g.Key.Length > 0 && g.Count() >= MinimumGroupSize)
            .ToList();

        var worthGrouping = tools.Count >= MinimumToolsForGrouping && qualifying.Count >= MinimumGroupCount;
        if (!worthGrouping) return tools.OrderBy(ToolSortKey, StringComparer.CurrentCultureIgnoreCase)
                                        .Select(ToolNode).ToList();

        var grouped = new HashSet<string>(qualifying.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        var nodes = new List<CatalogueNode>();

        foreach (var group in qualifying.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var members = group.OrderBy(ToolSortKey, StringComparer.CurrentCultureIgnoreCase).Select(ToolNode).ToList();
            var label = $"{NameHumanizer.Humanize(group.Key)}, {members.Count} item{(members.Count == 1 ? "" : "s")}";
            nodes.Add(new CatalogueNode(CatalogueNodeKind.Group, label, members));
        }

        // Tools whose prefix did not earn a group sit beside the groups rather than in a
        // "Miscellaneous" bucket nobody asked for.
        nodes.AddRange(tools
            .Where(t => !grouped.Contains(PrefixOf(t)))
            .OrderBy(ToolSortKey, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToolNode));

        return nodes;
    }

    private static CatalogueNode ToolNode(ToolInfo tool)
        => new(CatalogueNodeKind.Tool, tool.AccessibleName, tool: tool, helpText: tool.Summary);

    private static string ToolSortKey(ToolInfo tool) => tool.DisplayName;

    /// <summary>
    /// The part before the first <c>_</c>, <c>.</c> or <c>-</c>, when there is enough of it to mean
    /// something. <c>list_pull_requests</c> → <c>list</c>; <c>ping</c> → no prefix.
    /// </summary>
    public static string PrefixOf(ToolInfo tool)
    {
        var name = tool.Name;
        var index = name.IndexOfAny(['_', '.', '-']);
        if (index < 2) return "";

        var prefix = name[..index];
        return prefix.Length >= 2 ? prefix : "";
    }
}

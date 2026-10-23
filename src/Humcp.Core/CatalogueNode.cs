namespace Humcp.Core;

/// <summary>What a node in the main tree is. Categories and groups exist only when non-empty (§8).</summary>
public enum CatalogueNodeKind
{
    /// <summary>One configured server, carrying its connection status in its name.</summary>
    Server,

    /// <summary>Tools, Prompts, Resources.</summary>
    Category,

    /// <summary>A prefix group, when a server's tool names share one (§8).</summary>
    Group,

    /// <summary>An invocable tool.</summary>
    Tool,

    /// <summary>
    /// A prompt (Phase 12). Its node carries a <see cref="ToolInfo"/> of kind
    /// <see cref="OperationKind.Prompt"/>, so every command that opens a form opens one for it.
    /// </summary>
    Prompt,

    /// <summary>
    /// A resource or resource template (Phase 13), carrying a <see cref="ToolInfo"/> of that kind:
    /// Enter reads a resource, and opens a template's form.
    /// </summary>
    Resource,

    /// <summary>
    /// A sentence where nodes would be — "No servers are configured" — because an empty tree cannot
    /// take focus at all, and a pane the caret cannot enter is a pane that says nothing.
    /// </summary>
    Message,
}

/// <summary>
/// A node of the servers-and-tools tree. Immutable: the tree is rebuilt when a server's state
/// changes, which is cheap at this size and keeps the UI free of half-updated nodes.
/// </summary>
public sealed class CatalogueNode
{
    public CatalogueNode(
        CatalogueNodeKind kind,
        string accessibleName,
        IReadOnlyList<CatalogueNode>? children = null,
        ToolInfo? tool = null,
        string? helpText = null,
        string? serverId = null)
    {
        Kind = kind;
        ServerId = serverId;
        AccessibleName = accessibleName;
        Children = children ?? [];
        Tool = tool;
        HelpText = helpText ?? "";
    }

    public CatalogueNodeKind Kind { get; }

    /// <summary>
    /// The single string a screen reader reads on arrow-through, status and counts included — the
    /// shape Phase 0 confirmed reads well (§8, and phase0-findings §1).
    /// </summary>
    public string AccessibleName { get; }

    /// <summary>First sentence of a tool's description, for <c>AutomationProperties.HelpText</c>.</summary>
    public string HelpText { get; }

    /// <summary>
    /// The configuration key of the server this node belongs to — set on the server's own node and
    /// on everything under it, so a command run from any node knows which connection it is about.
    /// </summary>
    public string? ServerId { get; }

    /// <summary>Set on tool, prompt and resource nodes only.</summary>
    public ToolInfo? Tool { get; }

    public IReadOnlyList<CatalogueNode> Children { get; }

    public IEnumerable<CatalogueNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var descendant in Children.SelectMany(c => c.SelfAndDescendants()))
            yield return descendant;
    }

    public override string ToString() => AccessibleName;
}

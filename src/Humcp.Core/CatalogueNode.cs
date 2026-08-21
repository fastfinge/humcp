namespace Humcp.Core;

/// <summary>What a node in the main tree is. Categories and groups exist only when non-empty (§8).</summary>
public enum CatalogueNodeKind
{
    /// <summary>One configured server, carrying its connection status in its name.</summary>
    Server,

    /// <summary>Tools, Prompts, Resources. Phase 1 only ever builds Tools.</summary>
    Category,

    /// <summary>A prefix group, when a server's tool names share one (§8).</summary>
    Group,

    /// <summary>An invocable tool.</summary>
    Tool,
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
        string? helpText = null)
    {
        Kind = kind;
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

    /// <summary>Set on <see cref="CatalogueNodeKind.Tool"/> nodes only.</summary>
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

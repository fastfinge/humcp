using System.Text.Json;

namespace Humcp.Schema;

/// <summary>
/// How far a schema node got before the renderer ran out of ways to say it (ROADMAP §5). The
/// invariant is that it always gets somewhere: there is no fourth value meaning "cannot render".
/// </summary>
public enum FieldTier
{
    /// <summary>One schema node, one native control. A strict superset of the elicitation subset.</summary>
    Direct = 1,

    /// <summary>Recursive but still fully typed: objects and arrays of objects.</summary>
    Composite = 2,

    /// <summary>A JSON editor with live validation. Ugly, honest, and never a dead end.</summary>
    Raw = 3,
}

/// <summary>
/// What kind of control the field wants. Deliberately named for the shape of the data rather than
/// for a WPF type: <see cref="Choice"/>, not <c>ComboBox</c>. Phase 3 owns the mapping, and a second
/// capability source (ROADMAP §12) must not have to learn WPF vocabulary to use this.
/// </summary>
public enum FieldKind
{
    /// <summary>Single-line text.</summary>
    Text,

    /// <summary>Text expected to run long — a multi-line box, still one value.</summary>
    MultilineText,

    /// <summary>A closed set of values, from <c>enum</c>, <c>const</c>, or a union of constants.</summary>
    Choice,

    /// <summary>A number with a fractional part allowed.</summary>
    Number,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A yes/no.</summary>
    Boolean,

    /// <summary>A calendar date, no time of day.</summary>
    Date,

    /// <summary>A date and a time of day.</summary>
    DateTime,

    /// <summary>A list of primitives, edited as a list of values.</summary>
    PrimitiveList,

    /// <summary>An object: a real UIA group holding nested fields.</summary>
    Group,

    /// <summary>A list of objects, each row a sub-form.</summary>
    ObjectList,

    /// <summary>Whatever could not be typed. A JSON editor, validated live.</summary>
    Raw,
}

/// <summary>
/// One input, described in terms a UI can render and a screen reader can read out, with no
/// vocabulary from the protocol that produced it (ROADMAP §6.1, hedge 2).
/// </summary>
/// <remarks>
/// Every string here has already been through <see cref="TextSanitizer"/>: these are third-party
/// strings on their way to UI Automation and a synthesiser (§4, pitfall 5).
/// </remarks>
public sealed record FieldModel
{
    /// <summary>
    /// The property name to send, exactly as the schema spelled it. Empty for a list's item
    /// template, which has no name of its own.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Where this field sits in the argument object, as a dotted path — <c>filters.tags[]</c>.
    /// Stable across a schema's lifetime, so it is what MRU history (§4, pitfall 3) and validation
    /// errors are keyed by.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>What the control is called. From <c>title</c>, or the humanised property name.</summary>
    public required string Label { get; init; }

    public required FieldKind Kind { get; init; }

    public required FieldTier Tier { get; init; }

    /// <summary>
    /// First sentence of the description, for <c>AutomationProperties.HelpText</c>. Does not
    /// include the constraint summary — Phase 3 decides how to combine them, since a template
    /// knows what its control already announces.
    /// </summary>
    public string HelpText { get; init; } = "";

    /// <summary>The whole description, line structure intact, for the F1 details window.</summary>
    public string FullDescription { get; init; } = "";

    /// <summary>
    /// Named in the parent's <c>required</c> array. Drives
    /// <c>AutomationProperties.IsRequiredForForm</c> — never a "(required)" suffix on the label
    /// (§7.4).
    /// </summary>
    public bool IsRequired { get; init; }

    public FieldConstraints Constraints { get; init; } = FieldConstraints.None;

    /// <summary>The options, for <see cref="FieldKind.Choice"/>. Empty otherwise.</summary>
    public IReadOnlyList<ChoiceOption> Choices { get; init; } = [];

    /// <summary>
    /// Nested fields, for <see cref="FieldKind.Group"/> and for each row of
    /// <see cref="FieldKind.ObjectList"/>. Empty otherwise.
    /// </summary>
    public IReadOnlyList<FieldModel> Fields { get; init; } = [];

    /// <summary>The element template, for <see cref="FieldKind.PrimitiveList"/>.</summary>
    public FieldModel? ItemField { get; init; }

    /// <summary>
    /// The resolved schema node this field came from, kept for two jobs: seeding and validating the
    /// Tier 3 editor, and Copy Schema in the context menu (§7 keyboard map).
    /// </summary>
    public JsonElement Schema { get; init; }

    /// <summary>The schema's <c>default</c>, if it stated one.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>
    /// Why this field is not at the tier it looks like it should be — "a union of three object
    /// shapes", "recursive schema". Present exactly when <see cref="Tier"/> is
    /// <see cref="FieldTier.Raw"/>, and it is both the user-facing explanation (§7.18: declining
    /// says why) and the telemetry §5 asks for.
    /// </summary>
    public string? DegradedReason { get; init; }

    /// <summary>
    /// <c>null</c> is a permitted value: the schema said <c>type: ["string", "null"]</c> or offered
    /// a null branch. An empty box means null rather than an empty string.
    /// </summary>
    public bool AllowsNull { get; init; }

    /// <summary>The schema marked it <c>deprecated</c>. Phase 3's optional-field toggle hides these first.</summary>
    public bool IsDeprecated { get; init; }

    /// <summary>The schema marked it <c>readOnly</c> — present for display, not for the user to set.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Where this value belongs in the request being assembled, opaque to everything here
    /// (ROADMAP §6.1, hedge 3). MCP has one flat argument object and leaves it <c>null</c>;
    /// OpenAPI's five parameter destinations are what it exists for.
    /// </summary>
    public object? Binding { get; init; }

    /// <summary>This field and everything under it, parents before children.</summary>
    public IEnumerable<FieldModel> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in Descendants()) yield return child;
    }

    /// <summary>Everything under this field: group members, object-list columns, list items.</summary>
    public IEnumerable<FieldModel> Descendants()
    {
        foreach (var field in Fields)
            foreach (var node in field.SelfAndDescendants())
                yield return node;

        if (ItemField is not null)
            foreach (var node in ItemField.SelfAndDescendants())
                yield return node;
    }
}

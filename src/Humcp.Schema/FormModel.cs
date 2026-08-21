using System.Text.Json;

namespace Humcp.Schema;

/// <summary>
/// A whole set of inputs, ready to be rendered. What <see cref="FormBuilder"/> produces and what
/// Phase 3 binds templates to.
/// </summary>
public sealed record FormModel
{
    /// <summary>The top-level parameters, in the order the schema declared them (ROADMAP §7.7).</summary>
    public required IReadOnlyList<FieldModel> Fields { get; init; }

    /// <summary>The schema this came from, resolved no further than its root.</summary>
    public JsonElement Schema { get; init; }

    /// <summary>The root schema's own <c>title</c>, if it had one. Usually it does not.</summary>
    public string Title { get; init; } = "";

    /// <summary>The root schema's own <c>description</c>, first sentence.</summary>
    public string HelpText { get; init; } = "";

    /// <summary>
    /// Set when the argument object as a whole could not be broken into fields, and the form is a
    /// single JSON editor. The text says why, because §7.18 requires an edge to explain itself.
    /// </summary>
    public string? DegradedReason { get; init; }

    /// <summary>A tool that takes no arguments at all. Not a failure — most of Phase 1's tools.</summary>
    public bool IsEmpty => Fields.Count == 0;

    /// <summary>The whole form is one raw JSON editor (<see cref="DegradedReason"/> says why).</summary>
    public bool IsWhollyRaw => DegradedReason is not null;

    /// <summary>Top-level parameters and everything nested inside them.</summary>
    public IEnumerable<FieldModel> AllFields() => Fields.SelectMany(f => f.SelfAndDescendants());

    /// <summary>How many top-level parameters must be filled in.</summary>
    public int RequiredCount => Fields.Count(f => f.IsRequired);

    /// <summary>
    /// The worst tier anything in this form reached. A form that is entirely
    /// <see cref="FieldTier.Direct"/> is one the elicitation subset would have covered (§3).
    /// </summary>
    public FieldTier HighestTier => Fields.Count == 0
        ? FieldTier.Direct
        : AllFields().Max(f => f.Tier);

    internal static FormModel ForNoArguments(JsonElement schema) => new()
    {
        Fields = [],
        Schema = schema,
    };
}

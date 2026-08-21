using System.Text.Json;

namespace Humcp.Schema;

/// <summary>
/// One entry in a <see cref="FieldKind.Choice"/>. The label is what gets read out; the value is
/// what gets sent, in whatever JSON type the schema used.
/// </summary>
/// <remarks>
/// The two are separate because the elicitation subset says so — <c>enumNames</c> carries display
/// labels alongside <c>enum</c> values (ROADMAP §3) — and because a union of constants can carry a
/// <c>title</c> per branch. Where a schema offers no label, the value's own text is humanised, since
/// <c>PULL_REQUEST_REVIEW_COMMENT</c> is not a thing anyone wants to hear spelled out.
/// </remarks>
public sealed record ChoiceOption(string Label, JsonElement Value)
{
    /// <summary>
    /// The value as it would appear in JSON — <c>"draft"</c> becomes <c>draft</c>, numbers and
    /// booleans keep their text. This is the MRU key and what a text-entry fallback would match on.
    /// </summary>
    public string ValueText => Value.ValueKind switch
    {
        JsonValueKind.String => Value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => Value.GetRawText(),
    };

    /// <summary>True when this option means "not set" rather than a value.</summary>
    public bool IsNull => Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
}

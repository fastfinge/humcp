namespace Humcp.Schema;

/// <summary>
/// One thing wrong with one value.
/// </summary>
/// <param name="FieldPath">
/// The <see cref="FieldModel.Path"/> this belongs to, with list indices removed — <c>tags[]</c> —
/// so a form can find the control to put <c>AutomationProperties.ItemStatus</c> on.
/// </param>
/// <param name="InstancePath">
/// Where it actually is, indices included — <c>tags[2]</c>. What the error summary reads out, since
/// "the third tag" and "the tags" are different pieces of news.
/// </param>
/// <param name="Label">The field's label, or the property name where there is no field for it.</param>
/// <param name="Message">A whole sentence, written to be heard.</param>
/// <param name="Keyword">The schema keyword that objected. For tests and telemetry, never spoken.</param>
public sealed record ValidationIssue(
    string FieldPath,
    string InstancePath,
    string Label,
    string Message,
    string Keyword)
{
    /// <summary>What the error summary list shows for this entry.</summary>
    public string Announcement => Label.Length > 0 ? $"{Label}: {Message}" : Message;
}

/// <summary>
/// What client-side validation found. Advice, never a veto.
/// </summary>
/// <remarks>
/// ROADMAP §5: the server is the authority, and an over-strict client makes a working tool
/// unusable. Nothing here is a reason to refuse to send a call — it is a reason to say something
/// first. <see cref="WasChecked"/> exists so the difference between "we checked and it is fine" and
/// "we could not check" is never silently flattened into the same green.
/// </remarks>
public sealed record ValidationReport
{
    /// <summary>Everything checked out.</summary>
    public static readonly ValidationReport Clean = new() { Issues = [] };

    public required IReadOnlyList<ValidationIssue> Issues { get; init; }

    /// <summary>
    /// False when the schema itself defeated the validator — an unresolvable <c>$ref</c>, a
    /// construct the library rejects. The form still submits; it just submits unchecked.
    /// </summary>
    public bool WasChecked { get; init; } = true;

    /// <summary>Why nothing was checked, in a sentence, when <see cref="WasChecked"/> is false.</summary>
    public string? NotCheckedReason { get; init; }

    public bool IsValid => Issues.Count == 0;

    /// <summary>
    /// The line for the status region (§7): a count, or the single problem if there is only one.
    /// Deliberately not spoken through prism — a live region carries it, and prism on top of a live
    /// region says everything twice (§7's core rule).
    /// </summary>
    public string Summary => Issues.Count switch
    {
        0 when !WasChecked => NotCheckedReason ?? "These arguments could not be checked here.",
        0 => "No problems found.",
        1 => Issues[0].Announcement,
        var count => $"{count} problems found.",
    };

    /// <summary>The issues belonging to one field, for its <c>ItemStatus</c>.</summary>
    public IEnumerable<ValidationIssue> For(string fieldPath) =>
        Issues.Where(i => i.FieldPath == fieldPath);

    /// <summary>Where focus goes on submit-with-errors (§7.8), or <c>null</c> when nothing is wrong.</summary>
    public string? FirstInvalidPath => Issues.Count == 0 ? null : Issues[0].FieldPath;

    internal static ValidationReport Unchecked(string reason) => new()
    {
        Issues = [],
        WasChecked = false,
        NotCheckedReason = reason,
    };
}

using System.Text.Json;

namespace Humcp.Schema;

/// <summary>
/// What shape a piece of a result turned out to be, and therefore what it can be shown as
/// (ROADMAP §10, Phase 4). Named for the data rather than for a control, like
/// <see cref="FieldKind"/> and for the same reason (§6.1, hedge 2).
/// </summary>
public enum ResultShape
{
    /// <summary>One value: a string, a number, a boolean, or null.</summary>
    Value,

    /// <summary>An object. A named group of nested nodes.</summary>
    Properties,

    /// <summary>An array of anything but uniform objects. A numbered sequence of nested nodes.</summary>
    List,

    /// <summary>An array of objects, which is a table with columns and rows.</summary>
    Table,
}

/// <summary>One column of a <see cref="ResultShape.Table"/>.</summary>
/// <param name="Name">The property name, exactly as the data spelled it.</param>
/// <param name="Label">What the column header says, humanised or from the schema's <c>title</c>.</param>
/// <param name="HelpText">The schema's description of this property, if it gave one.</param>
public sealed record ResultColumn(string Name, string Label, string HelpText);

/// <summary>
/// One cell. <paramref name="Detail"/> is set exactly when the value underneath is an object or an
/// array — a grid cell can only hold a line of text, so the line is a summary and the whole value
/// stays reachable.
/// </summary>
public sealed record ResultCell(string Text, ResultNode? Detail)
{
    /// <summary>A cell holding a value too big to be a line of text, which has somewhere to go.</summary>
    public bool HasDetail => Detail is not null;
}

/// <summary>One row of a table. <paramref name="Number"/> is 1-based, and is what a reader says.</summary>
public sealed record ResultRow(int Number, IReadOnlyList<ResultCell> Cells)
{
    /// <summary>The row as one line, for a reader that meets it outside the grid.</summary>
    public string Summary => TextSanitizer.Clean(
        string.Join(", ", Cells.Select(c => c.Text).Where(t => t.Length > 0)), TextSanitizer.NameLength);

    /// <summary>
    /// The row, spoken. §7.23 with a grid this time: an item bound into a list-shaped control takes
    /// its accessible name from the plain text of its content, and for a bound object that is its
    /// <c>ToString</c> — so a record's default one announces every property it has, C# type names
    /// included. This was heard in the first Phase 4 pass as <em>"ResultRow { Number = 1, Cells =
    /// System.Collections.Generic.List`1[Humcp.Schema.ResultCell], Summary = …"</em> on every row.
    /// </summary>
    public override string ToString() => Summary;
}

/// <summary>
/// One node of a rendered result: what it is called, what shape it is, and what is under it
/// (ROADMAP §10, Phase 4).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="FieldModel"/> at the other end of a call, and pure for the same
/// reasons. What a result <em>says</em> — that this is a table of eight rows, that this string is
/// four thousand characters and wants a window rather than a line, that this node repeats what the
/// text above it already said — is a set of rules, and rules that can only be exercised by launching
/// a desktop application are rules that get exercised once (§6.3).
/// </para>
/// <para>
/// Every string here has been through <see cref="TextSanitizer"/>. A result is the most untrusted
/// text in the application: it is whatever a third-party server chose to answer with, and it is on
/// its way to UI Automation and a synthesiser (§4, pitfall 5).
/// </para>
/// </remarks>
public sealed record ResultNode
{
    /// <summary>What this node is called: the schema's <c>title</c>, or the humanised property name.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Where this node sits in the value, as a path — <c>entities[2].name</c>. Unique within one
    /// result, and what a "copy this" or "open this" command names.
    /// </summary>
    public required string Path { get; init; }

    public required ResultShape Shape { get; init; }

    /// <summary>The whole value as text, for <see cref="ResultShape.Value"/>. Empty otherwise.</summary>
    public string Text { get; init; } = "";

    /// <summary>What kind of value this is, in a word a person would use: "text", "number", "null".</summary>
    public string TypeName { get; init; } = "";

    /// <summary>The schema's description of this node, first sentence, if it described one.</summary>
    public string HelpText { get; init; } = "";

    /// <summary>
    /// A string long enough, or broken across enough lines, that it wants a window with a caret in
    /// it rather than a line in a tree (§7.9). The renderer's cue to offer one.
    /// </summary>
    public bool IsLongText { get; init; }

    /// <summary>The value this node was built from, for Copy and for the details window.</summary>
    public JsonElement Value { get; init; }

    /// <summary>Nested nodes, for <see cref="ResultShape.Properties"/> and <see cref="ResultShape.List"/>.</summary>
    public IReadOnlyList<ResultNode> Children { get; init; } = [];

    /// <summary>Columns, for <see cref="ResultShape.Table"/>.</summary>
    public IReadOnlyList<ResultColumn> Columns { get; init; } = [];

    /// <summary>The rows this node is showing, which is not always all of them.</summary>
    public IReadOnlyList<ResultRow> Rows { get; init; } = [];

    /// <summary>How many rows the data really has, before <see cref="ResultBuildOptions.MaxRows"/>.</summary>
    public int TotalRows { get; init; }

    /// <summary>
    /// Something under this node was left out — too many rows, too deep, or too much of it. Never
    /// silent: what a truncated node says is <em>that</em> it is truncated (§7.18).
    /// </summary>
    public bool IsTruncated { get; init; }

    /// <summary>
    /// Why this node is a lump of JSON rather than something taken apart. Set exactly when the
    /// builder gave up on a node, and worded for the person reading it, like
    /// <see cref="FieldModel.DegradedReason"/>.
    /// </summary>
    public string? DegradedReason { get; init; }

    /// <summary>
    /// The node a result opens on. A tree of somebody else's data is opened one level at a time by
    /// whoever is reading it; the root is the one level that is not a choice, since a tree with
    /// nothing showing is a tree with nothing to arrow to.
    /// </summary>
    public bool IsRoot => Path.Length == 0;

    /// <summary>What this node is, in as few words as carry the fact.</summary>
    public string Summary => Shape switch
    {
        ResultShape.Value when IsLongText => Count(Text.Length, "character", "characters"),
        ResultShape.Value => Text.Length == 0 ? "empty" : Text,
        ResultShape.Properties => Count(Children.Count, "field", "fields"),
        ResultShape.List => Count(Children.Count, "item", "items"),
        ResultShape.Table => $"table, {Count(TotalRows, "row", "rows")}, " +
                             $"{Count(Columns.Count, "column", "columns")}",
        _ => "",
    };

    /// <summary>
    /// What a screen reader says when the caret lands on this node. The label and the value in one
    /// breath, because a tree node is read as one thing and a name that says only half of it makes
    /// the user press a key to hear the rest.
    /// </summary>
    public string AccessibleName => Label.Length == 0 ? Summary : $"{Label}, {Summary}";

    /// <summary>This node and everything under it, parents before children.</summary>
    public IEnumerable<ResultNode> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;

        foreach (var cell in Rows.SelectMany(r => r.Cells))
            if (cell.Detail is { } detail)
                foreach (var node in detail.SelfAndDescendants())
                    yield return node;
    }

    /// <summary>The value as indented JSON, for Copy and for the details window.</summary>
    public string Json()
    {
        if (Value.ValueKind == JsonValueKind.Undefined) return "";

        try
        {
            return JsonSerializer.Serialize(Value, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return $"(could not be formatted: {ex.Message})";
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Count(int count, string singular, string plural) =>
        $"{count:N0} {(count == 1 ? singular : plural)}";
}

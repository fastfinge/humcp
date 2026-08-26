using System.Text.Json;

namespace Humcp.Schema;

/// <summary>Limits on how far a result is allowed to push the window showing it.</summary>
/// <remarks>
/// Guards against a hostile or merely enormous answer, not tuning knobs — the same rule as
/// <see cref="FormBuildOptions"/>, from the other end of the call. A result arrives from a third
/// party and can be a hundred thousand rows deep in nested objects. Every limit here turns that into
/// something a screen reader can still walk, and every one of them says so when it bites (§7.18).
/// </remarks>
public sealed record ResultBuildOptions
{
    public static readonly ResultBuildOptions Default = new();

    /// <summary>How far down a value may be taken apart before a node becomes plain JSON.</summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>How many nodes one result may produce in total.</summary>
    public int MaxNodes { get; init; } = 5_000;

    /// <summary>
    /// How many rows of a table are built. Beyond this the node still reports the real total, so
    /// "200 of 40,000 rows" is a fact the user is told rather than one the window hides.
    /// </summary>
    /// <remarks>
    /// Two hundred rather than a round thousand, and the number comes from §7.6 rather than from
    /// taste. A grid showing these rows has both kinds of virtualization off, because a virtualized
    /// row is not in the UI Automation tree at all — so every row here is a real one, built and
    /// kept. The cap and that setting are two halves of one decision: raise this and the grid stops
    /// being quick; turn virtualization on to make it quick again and the rows stop being reachable.
    /// </remarks>
    public int MaxRows { get; init; } = 200;

    /// <summary>How many columns a table may have before the rest are left out.</summary>
    public int MaxColumns { get; init; } = 30;

    /// <summary>
    /// Above this many characters — or at the first line break — a string stops being a line in a
    /// tree and becomes text wanting a caret (ROADMAP §7.9).
    /// </summary>
    public int LongTextThreshold { get; init; } = 200;

    /// <summary>How much of a value fits in one cell of a table.</summary>
    public int MaxCellLength { get; init; } = 200;
}

/// <summary>
/// The other half of the schema engine: a value in, a <see cref="ResultNode"/> out (ROADMAP §10,
/// Phase 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes a schema and a value, and the schema is optional</b> — §6.1's fifth hedge asks for
/// exactly this signature so that a second capability source can render results without owning a
/// protocol type, and the corpus says the optionality is the important half: <b>25 of 213 tools
/// publish an <c>outputSchema</c> at all</b>. A renderer that needs one would have nothing to show
/// for the other 188. So the shape is read from the value, and the schema, when there is one, adds
/// names and descriptions to it.
/// </para>
/// <para>
/// <b>The invariant is <see cref="FormBuilder"/>'s, unchanged: it never fails.</b> There is no value
/// for which this throws, and no value it refuses to show. Anything it cannot take apart becomes a
/// node holding the JSON, with a sentence saying why — the same answer Tier 3 gives on the way in.
/// </para>
/// </remarks>
public static class ResultBuilder
{
    /// <summary>
    /// Builds the tree for one value. Safe to call with anything, including
    /// <see cref="JsonValueKind.Undefined"/> and a schema that describes something else entirely.
    /// </summary>
    /// <param name="schema">The value's schema, if its source published one.</param>
    /// <param name="value">The value itself.</param>
    /// <param name="label">What the root node is called.</param>
    /// <param name="options">Limits; <see cref="ResultBuildOptions.Default"/> when omitted.</param>
    public static ResultNode Build(
        JsonElement? schema,
        JsonElement value,
        string label = "Result",
        ResultBuildOptions? options = null)
    {
        // Detached from whatever document they arrived in, for the reason FormBuilder detaches an
        // input schema: the SDK's element belongs to a response the client is free to drop, and a
        // ResultNode outlives the call that produced it.
        var root = value.ValueKind == JsonValueKind.Undefined ? value : value.Clone();
        var schemaRoot = schema is { ValueKind: not JsonValueKind.Undefined } given ? given.Clone() : (JsonElement?)null;

        return new Builder(schemaRoot, options ?? ResultBuildOptions.Default)
            .BuildRoot(TextSanitizer.Clean(label, TextSanitizer.NameLength), root);
    }

    /// <summary>
    /// Whether a structured result says nothing the text content did not already say.
    /// </summary>
    /// <remarks>
    /// Thirteen of the corpus's 25 output schemas are <c>{ "content": { "type": "string" } }</c> — a
    /// wrapper the server fills with the same string it already sent as text, because SEP-2106 asks
    /// for an object and the tool returns a string. Rendering both would say everything twice, which
    /// is the defect Phase 1 found by ear in a different place (phase1-notes 2.7) and the reason
    /// §7's core rule is written the way it is. So the client checks, and the announcement says
    /// which of the two it is showing.
    /// </remarks>
    public static bool RepeatsText(JsonElement structured, string text)
    {
        var expected = text.Trim();
        if (expected.Length == 0) return false;

        return structured.ValueKind switch
        {
            JsonValueKind.String => Same(structured, expected),

            JsonValueKind.Object when structured.EnumerateObject().Count() == 1 =>
                Same(structured.EnumerateObject().First().Value, expected),

            _ => false,
        };
    }

    private static bool Same(JsonElement value, string expected) =>
        value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString()?.Trim(), expected, StringComparison.Ordinal);

    private sealed class Builder(JsonElement? schemaRoot, ResultBuildOptions options)
    {
        private readonly SchemaResolver? _resolver =
            schemaRoot is { } root ? new SchemaResolver(root) : null;

        private int _remaining = options.MaxNodes;

        /// <summary>
        /// The root, with a wrapper of one property taken off it first.
        /// </summary>
        /// <remarks>
        /// SEP-2106 still asks an <c>outputSchema</c> to describe an object for clients on older
        /// revisions, so a tool whose answer is a list or a string publishes
        /// <c>{ "entities": [ … ] }</c> — and the wrapper is the specification's rather than the
        /// server's. It is most of what the corpus publishes: 17 of the 25 output schemas are one
        /// property holding the answer, and 13 of those are a bare string. Rendering the wrapper puts
        /// a level of nesting and a keystroke in front of almost every structured result there is,
        /// and the property's own name — "Entities", "Content" — says more than "Structured result,
        /// 1 field" ever could.
        /// </remarks>
        internal ResultNode BuildRoot(string label, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var members = value.EnumerateObject().ToList();

                if (members.Count == 1)
                {
                    var only = members[0];
                    return Build(Describe(schemaRoot, only.Name).Label, "", only.Value,
                        Member(schemaRoot, only.Name), 0);
                }
            }

            return Build(label, "", value, schemaRoot, 0);
        }

        /// <summary>
        /// One node, with its schema followed through any <c>$ref</c> first. A reference that cannot
        /// be followed costs a label and a description and nothing else: the value is still there,
        /// and the value is what the user came for.
        /// </summary>
        private ResultNode Build(string label, string path, JsonElement value, JsonElement? schema, int depth)
        {
            _remaining--;

            var pushed = 0;
            var resolved = schema;

            if (schema is { } node && _resolver is { } resolver)
            {
                var resolution = resolver.Resolve(node);
                resolved = resolution.Ok ? resolution.Node : null;
                pushed = resolution.PointersPushed;
            }

            try
            {
                return BuildNode(label, path, value, resolved, depth);
            }
            finally
            {
                _resolver?.Release(pushed);
            }
        }

        private ResultNode BuildNode(string label, string path, JsonElement value, JsonElement? schema, int depth)
        {
            var help = schema is { } node ? TextSanitizer.FirstSentence(RawText(node, "description")) : "";

            if (depth >= options.MaxDepth)
                return Raw(label, path, value, help, "it is nested deeper than a window can show");

            if (_remaining <= 0)
                return Raw(label, path, value, help, "the result is larger than one window can hold");

            return value.ValueKind switch
            {
                JsonValueKind.Object => BuildProperties(label, path, value, schema, help, depth),
                JsonValueKind.Array => BuildArray(label, path, value, schema, help, depth),
                _ => BuildValue(label, path, value, help),
            };
        }

        // ------------------------------------------------------------------ one value

        private ResultNode BuildValue(string label, string path, JsonElement value, string help)
        {
            var (raw, type) = value.ValueKind switch
            {
                JsonValueKind.String => (value.GetString() ?? "", "text"),
                JsonValueKind.Number => (value.GetRawText(), "number"),
                JsonValueKind.True => ("true", "true or false"),
                JsonValueKind.False => ("false", "true or false"),
                JsonValueKind.Null => ("null", "null"),
                _ => ("", "nothing"),
            };

            // A string that runs long, or that has lines in it, is text rather than a value: it
            // wants the caret of §7.9 and not a tree node holding four thousand characters.
            var isLong = value.ValueKind == JsonValueKind.String
                         && (raw.Length > options.LongTextThreshold
                             || raw.Contains('\n', StringComparison.Ordinal));

            return new ResultNode
            {
                Label = label,
                Path = path,
                Shape = ResultShape.Value,
                TypeName = type,
                HelpText = help,
                IsLongText = isLong,
                IsTruncated = raw.Length > TextSanitizer.ResultLength,
                Value = value,
                Text = isLong
                    ? TextSanitizer.CleanMultiline(raw, TextSanitizer.ResultLength)
                    : TextSanitizer.Clean(raw, TextSanitizer.NameLength),
            };
        }

        /// <summary>A node the builder gave up on, holding its JSON and the reason it did.</summary>
        private static ResultNode Raw(string label, string path, JsonElement value, string help, string reason)
        {
            var node = new ResultNode
            {
                Label = label,
                Path = path,
                Shape = ResultShape.Value,
                TypeName = "JSON",
                HelpText = help,
                IsLongText = true,
                IsTruncated = true,
                DegradedReason = reason,
                Value = value,
            };

            return node with { Text = TextSanitizer.CleanMultiline(node.Json(), TextSanitizer.ResultLength) };
        }

        // ------------------------------------------------------------------ objects

        private ResultNode BuildProperties(
            string label, string path, JsonElement value, JsonElement? schema, string help, int depth)
        {
            var children = new List<ResultNode>();
            var truncated = false;

            foreach (var property in value.EnumerateObject())
            {
                if (_remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                children.Add(Build(
                    Describe(schema, property.Name).Label,
                    Extend(path, property.Name),
                    property.Value,
                    Member(schema, property.Name),
                    depth + 1));
            }

            return new ResultNode
            {
                Label = label,
                Path = path,
                Shape = ResultShape.Properties,
                TypeName = "group",
                HelpText = help,
                IsTruncated = truncated,
                Value = value,
                Children = children,
            };
        }

        // ------------------------------------------------------------------ arrays

        private ResultNode BuildArray(
            string label, string path, JsonElement value, JsonElement? schema, string help, int depth)
        {
            var items = Items(schema);

            return IsTable(value)
                ? BuildTable(label, path, value, items, help, depth)
                : BuildList(label, path, value, items, help, depth);
        }

        /// <summary>
        /// A table is an array whose every element is an object. Nothing else qualifies: an array
        /// mixing objects and strings has no columns, and pretending otherwise would put a
        /// row-shaped hole in the middle of a grid.
        /// </summary>
        private static bool IsTable(JsonElement array)
        {
            var any = false;

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) return false;
                any = true;
            }

            return any;
        }

        private ResultNode BuildList(
            string label, string path, JsonElement value, JsonElement? items, string help, int depth)
        {
            var children = new List<ResultNode>();
            var index = 0;
            var truncated = false;

            foreach (var element in value.EnumerateArray())
            {
                if (_remaining <= 0 || index >= options.MaxRows)
                {
                    truncated = true;
                    break;
                }

                index++;
                children.Add(Build($"Item {index}", $"{path}[{index - 1}]", element, items, depth + 1));
            }

            return new ResultNode
            {
                Label = label,
                Path = path,
                Shape = ResultShape.List,
                TypeName = "list",
                HelpText = help,
                IsTruncated = truncated,
                Value = value,
                Children = children,
            };
        }

        private ResultNode BuildTable(
            string label, string path, JsonElement value, JsonElement? items, string help, int depth)
        {
            var pushed = 0;
            var itemSchema = items;

            if (items is { } node && _resolver is { } resolver)
            {
                var resolution = resolver.Resolve(node);
                itemSchema = resolution.Ok ? resolution.Node : null;
                pushed = resolution.PointersPushed;
            }

            try
            {
                return BuildTableRows(label, path, value, itemSchema, help, depth);
            }
            finally
            {
                _resolver?.Release(pushed);
            }
        }

        private ResultNode BuildTableRows(
            string label, string path, JsonElement value, JsonElement? items, string help, int depth)
        {
            var columns = Columns(value, items);
            var rows = new List<ResultRow>();
            var total = 0;
            var truncated = columns.Truncated;

            foreach (var element in value.EnumerateArray())
            {
                total++;

                if (rows.Count >= options.MaxRows || _remaining <= 0)
                {
                    truncated = true;
                    continue;
                }

                var cells = new List<ResultCell>(columns.Names.Count);

                foreach (var column in columns.Names)
                {
                    cells.Add(element.TryGetProperty(column.Name, out var cell)
                        ? BuildCell(column, $"{path}[{rows.Count}].{column.Name}", cell, Member(items, column.Name), depth)
                        : new ResultCell("", null));
                }

                rows.Add(new ResultRow(rows.Count + 1, cells));
            }

            return new ResultNode
            {
                Label = label,
                Path = path,
                Shape = ResultShape.Table,
                TypeName = "table",
                HelpText = help,
                IsTruncated = truncated,
                Value = value,
                Columns = columns.Names,
                Rows = rows,
                TotalRows = total,
            };
        }

        /// <summary>
        /// One cell. A cell can only be a line of text, so a value that is not one becomes a summary
        /// with the whole value hanging off it — the grid stays a grid, and nothing is dropped.
        /// </summary>
        private ResultCell BuildCell(
            ResultColumn column, string path, JsonElement value, JsonElement? schema, int depth)
        {
            if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return new ResultCell(CellText(value), null);

            var detail = Build(column.Label, path, value, schema, depth + 1);
            return new ResultCell(detail.Summary, detail);
        }

        private string CellText(JsonElement value)
        {
            var raw = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Null => "null",
                _ => value.GetRawText(),
            };

            return TextSanitizer.Clean(raw, options.MaxCellLength);
        }

        /// <summary>
        /// The columns, in the schema's order where there is a schema and in first-seen order for
        /// anything it did not mention. A schema is a description of what the server meant to send,
        /// and the order it declared its properties in is the order it thinks they read best; a
        /// property that turned up anyway is still shown, because the data is the authority on what
        /// arrived.
        /// </summary>
        private (IReadOnlyList<ResultColumn> Names, bool Truncated) Columns(JsonElement array, JsonElement? items)
        {
            var present = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var scanned = 0;

            foreach (var element in array.EnumerateArray())
            {
                if (scanned++ >= options.MaxRows) break;

                foreach (var property in element.EnumerateObject())
                    if (seen.Add(property.Name))
                        present.Add(property.Name);
            }

            var ordered = new List<string>();

            if (items is { } node
                && node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                    if (seen.Contains(property.Name))
                        ordered.Add(property.Name);
            }

            foreach (var name in present)
                if (!ordered.Contains(name, StringComparer.Ordinal))
                    ordered.Add(name);

            var truncated = ordered.Count > options.MaxColumns;
            if (truncated) ordered = ordered[..options.MaxColumns];

            return (
                [.. ordered.Select(name =>
                {
                    var (label, help) = Describe(items, name);
                    return new ResultColumn(name, label, help);
                })],
                truncated);
        }

        // ------------------------------------------------------------------ schema lookups

        /// <summary>The schema for one property of an object schema, or for anything it allows.</summary>
        private static JsonElement? Member(JsonElement? schema, string name)
        {
            if (schema is not { ValueKind: JsonValueKind.Object } node) return null;

            if (node.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(name, out var member))
            {
                return member;
            }

            return node.TryGetProperty("additionalProperties", out var extra)
                   && extra.ValueKind == JsonValueKind.Object
                ? extra
                : null;
        }

        private static JsonElement? Items(JsonElement? schema)
        {
            if (schema is not { ValueKind: JsonValueKind.Object } node) return null;

            return node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                ? items
                : null;
        }

        /// <summary>
        /// What to call one property and what to say about it: the schema's <c>title</c> and
        /// <c>description</c>, or the property name made pronounceable. The same rule as a field's
        /// label (§7.1), because it is read out the same way.
        /// </summary>
        /// <remarks>
        /// The reference is followed first, and that is the whole reason this is not two lines. The
        /// C# SDK publishes every nested object as a <c>$ref</c> into <c>$defs</c> (phase2-notes
        /// 3.2), so a title read off the unresolved node is a title that is never found on the
        /// servers most likely to have written one.
        /// </remarks>
        private (string Label, string Help) Describe(JsonElement? schema, string name)
        {
            if (Member(schema, name) is not { } member)
                return (NameHumanizer.Humanize(name), "");

            var pushed = 0;
            var node = member;

            if (_resolver is { } resolver)
            {
                var resolution = resolver.Resolve(member);
                if (resolution.Ok) node = resolution.Node;
                pushed = resolution.PointersPushed;
            }

            try
            {
                var title = Text(node, "title", TextSanitizer.NameLength);

                return (
                    title.Length > 0 ? title : NameHumanizer.Humanize(name),
                    TextSanitizer.FirstSentence(RawText(node, "description")));
            }
            finally
            {
                _resolver?.Release(pushed);
            }
        }

        private static string Extend(string path, string name) =>
            path.Length == 0 ? name : $"{path}.{name}";

        private static string Text(JsonElement node, string property, int maxLength) =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? TextSanitizer.Clean(value.GetString(), maxLength)
                : "";

        private static string? RawText(JsonElement node, string property) =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

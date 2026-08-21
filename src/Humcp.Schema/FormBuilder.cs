using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humcp.Schema;

/// <summary>Limits on how far a schema is allowed to push the form it produces.</summary>
/// <remarks>
/// These are guards against hostile input (ROADMAP §9), not tuning knobs. A schema arrives from a
/// third party and may nest a hundred deep, or fan out through <c>$defs</c> until it is exponential
/// in the document's size. Both hit a limit and become Tier 3, which is a form a user can still
/// submit; neither hangs the machine.
/// </remarks>
public sealed record FormBuildOptions
{
    public static readonly FormBuildOptions Default = new();

    /// <summary>How many objects deep a form may go before nesting becomes a JSON editor.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>
    /// How many fields one form may hold. Phase 0 measured 209 on one form and found it navigable;
    /// this is well past anything a human would fill in, and exists so a pathological schema
    /// degrades instead of expanding forever.
    /// </summary>
    public int MaxFields { get; init; } = 1000;

    /// <summary>
    /// The <c>maxLength</c> above which a string gets a multi-line box (ROADMAP §5, Tier 1).
    /// </summary>
    public int MultilineThreshold { get; init; } = 200;
}

/// <summary>
/// The schema engine: a JSON Schema in, a <see cref="FormModel"/> out (ROADMAP §5, §6.1 hedge 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant is that it never fails.</b> There is no input for which this throws or returns
/// nothing — every schema produces a form, and a schema this code cannot take apart produces a
/// single JSON editor with a sentence saying why. That is the whole point of the tiers: a tool
/// whose arguments we cannot model is still a tool the user can call.
/// </para>
/// <para>
/// What comes out describes <em>shape</em> only. Nothing here decides whether a value is
/// acceptable — <see cref="SchemaValidator"/> does that, against the untouched original schema, so
/// the simplifications this builder makes (merging <c>allOf</c>, unwrapping nullable unions) can
/// never widen or narrow what the server will accept.
/// </para>
/// </remarks>
public static class FormBuilder
{
    /// <summary>
    /// Builds the form for one operation's input schema. Safe to call with anything at all,
    /// including <see cref="JsonValueKind.Undefined"/>.
    /// </summary>
    public static FormModel Build(JsonElement schema, FormBuildOptions? options = null)
    {
        // Detached from whatever document it arrived in. The SDK's JsonElement belongs to a document
        // owned by the client's tool list, and a FieldModel outlives the listing that produced it.
        var root = schema.ValueKind == JsonValueKind.Undefined ? schema : schema.Clone();

        return new Builder(root, options ?? FormBuildOptions.Default).BuildForm();
    }

    private sealed class Builder(JsonElement root, FormBuildOptions options)
    {
        private readonly SchemaResolver _resolver = new(root);
        private int _remainingFields = options.MaxFields;

        internal FormModel BuildForm()
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                // A boolean schema, or no schema at all. `true` accepts any arguments; the honest
                // presentation of "anything" is an editor, not a guess at fields.
                return WhollyRaw("the server did not describe its arguments");
            }

            var resolution = _resolver.Resolve(root);
            if (!resolution.Ok) return WhollyRaw(resolution.Reason ?? "a reference that could not be followed");

            var node = resolution.Node;

            if (HasComposition(node))
                return WhollyRaw("arguments described as a choice between shapes rather than as fields");

            if (TypeNames(node) is { Count: > 0 } types && !types.Contains("object"))
                return WhollyRaw($"arguments described as {Join(types)} rather than as an object");

            // An object schema with no `properties` is the ordinary shape of a tool that takes no
            // arguments, and that is a form with nothing on it — not a degraded one. This is the one
            // place where a missing `properties` means "none" rather than "unspecified"; nested
            // objects read it the other way (see BuildGroup).
            if (!node.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return FormModel.ForNoArguments(root);
            }

            var required = RequiredNames(node);
            var fields = new List<FieldModel>();

            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(BuildField(
                    name: property.Name,
                    path: property.Name,
                    raw: property.Value,
                    isRequired: required.Contains(property.Name),
                    depth: 0));
            }

            return new FormModel
            {
                Fields = fields,
                Schema = root,
                Title = Text(node, "title", TextSanitizer.NameLength),
                HelpText = TextSanitizer.FirstSentence(RawText(node, "description")),
            };
        }

        private FormModel WhollyRaw(string reason) => new()
        {
            Fields =
            [
                new FieldModel
                {
                    Name = "",
                    Path = "",
                    Label = "Arguments",
                    Kind = FieldKind.Raw,
                    Tier = FieldTier.Raw,
                    HelpText = $"Edit the arguments as JSON. This tool describes {reason}.",
                    Schema = root,
                    DegradedReason = reason,
                },
            ],
            Schema = root,
            DegradedReason = reason,
        };

        // ------------------------------------------------------------------ one field

        private FieldModel BuildField(string name, string path, JsonElement raw, bool isRequired, int depth)
        {
            if (_remainingFields-- <= 0)
                return Degraded(name, path, raw, isRequired, "more fields than one form can hold");

            var pushed = 0;

            try
            {
                var resolution = _resolver.Resolve(raw);
                if (!resolution.Ok)
                    return Degraded(name, path, raw, isRequired, resolution.Reason ?? "a reference that could not be followed");

                pushed += resolution.PointersPushed;
                var node = resolution.Node;

                if (node.ValueKind == JsonValueKind.False)
                    return Degraded(name, path, node, isRequired, "a value the schema forbids entirely");

                if (node.ValueKind != JsonValueKind.Object)
                    return Degraded(name, path, node, isRequired, "no constraints at all");

                var allowsNull = false;
                node = FlattenAllOf(node, ref pushed);
                node = UnwrapNullableUnion(node, ref pushed, ref allowsNull);

                if (depth >= options.MaxDepth)
                    return Degraded(name, path, node, isRequired, "nesting deeper than a form can show");

                return Shape(name, path, node, isRequired, allowsNull, depth);
            }
            finally
            {
                _resolver.Release(pushed);
            }
        }

        /// <summary>Everything after <c>$ref</c>, <c>allOf</c> and nullable unions have been dealt with.</summary>
        private FieldModel Shape(
            string name, string path, JsonElement node, bool isRequired, bool allowsNull, int depth)
        {
            if (HasComposition(node))
            {
                // A union of constants is an enumeration wearing a different hat, and a common one:
                // it is what a discriminated choice looks like when each branch carries a title.
                if (ConstantUnion(node, ref allowsNull) is { Count: > 0 } options)
                    return Field(name, path, node, isRequired, FieldKind.Choice, FieldTier.Direct) with
                    {
                        Choices = options,
                        AllowsNull = allowsNull,
                    };

                return Degraded(name, path, node, isRequired, CompositionReason(node));
            }

            if (Enumeration(node) is { } enumeration)
            {
                return enumeration.Count == 0
                    ? Degraded(name, path, node, isRequired, "a list of permitted values that is empty")
                    : Field(name, path, node, isRequired, FieldKind.Choice, FieldTier.Direct) with
                    {
                        Choices = enumeration,
                        AllowsNull = allowsNull || enumeration.Any(o => o.IsNull),
                    };
            }

            allowsNull |= DeclaresNull(node);

            var types = TypeNames(node);
            if (types.Count > 1)
                return Degraded(name, path, node, isRequired, $"more than one type at once — {Join(types)}");

            var type = types.Count == 1 ? types[0] : InferType(node);

            return type switch
            {
                "string" => Field(name, path, node, isRequired, StringKind(node), FieldTier.Direct)
                    with { AllowsNull = allowsNull },
                "integer" => Field(name, path, node, isRequired, FieldKind.Integer, FieldTier.Direct)
                    with { AllowsNull = allowsNull },
                "number" => Field(name, path, node, isRequired, FieldKind.Number, FieldTier.Direct)
                    with { AllowsNull = allowsNull },
                "boolean" => Field(name, path, node, isRequired, FieldKind.Boolean, FieldTier.Direct)
                    with { AllowsNull = allowsNull },
                "array" => BuildList(name, path, node, isRequired, allowsNull, depth),
                "object" => BuildGroup(name, path, node, isRequired, allowsNull, depth),
                "null" => Degraded(name, path, node, isRequired, "a value that can only ever be null"),
                _ => Degraded(name, path, node, isRequired, "a value whose type the schema never states"),
            };
        }

        private FieldModel BuildGroup(
            string name, string path, JsonElement node, bool isRequired, bool allowsNull, int depth)
        {
            // The mirror of the rule at the root: down here, an object with no listed properties is
            // a free-form map — "any keys you like" — and a form has no way to draw that. The root
            // gets the opposite reading because a tool with no arguments has to spell that somehow.
            if (!node.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !properties.EnumerateObject().Any())
            {
                return Degraded(name, path, node, isRequired, "an object with any keys at all, none of them named");
            }

            var required = RequiredNames(node);
            var fields = new List<FieldModel>();

            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(BuildField(
                    name: property.Name,
                    path: path.Length == 0 ? property.Name : $"{path}.{property.Name}",
                    raw: property.Value,
                    isRequired: required.Contains(property.Name),
                    depth: depth + 1));
            }

            return Field(name, path, node, isRequired, FieldKind.Group, FieldTier.Composite) with
            {
                Fields = fields,
                AllowsNull = allowsNull,
            };
        }

        private FieldModel BuildList(
            string name, string path, JsonElement node, bool isRequired, bool allowsNull, int depth)
        {
            if (node.TryGetProperty("prefixItems", out _))
                return Degraded(name, path, node, isRequired, "a list whose entries each have a different shape");

            if (!node.TryGetProperty("items", out var items) || items.ValueKind == JsonValueKind.True)
                return Degraded(name, path, node, isRequired, "a list whose contents the schema never describes");

            var item = BuildField("", $"{path}[]", items, isRequired: false, depth + 1);

            if (IsPrimitive(item.Kind) && item.Tier == FieldTier.Direct)
            {
                return Field(name, path, node, isRequired, FieldKind.PrimitiveList, FieldTier.Direct) with
                {
                    ItemField = item,
                    AllowsNull = allowsNull,
                };
            }

            if (item.Kind == FieldKind.Group)
            {
                // Both: Fields is the row's shape, which is what a sub-form binds to, and ItemField
                // keeps the row's own label and help text for the window that opens over it.
                return Field(name, path, node, isRequired, FieldKind.ObjectList, FieldTier.Composite) with
                {
                    Fields = item.Fields,
                    ItemField = item,
                    AllowsNull = allowsNull,
                };
            }

            return Degraded(name, path, node, isRequired,
                item.DegradedReason is { } reason ? $"a list of {reason}" : "a list of lists");
        }

        // ------------------------------------------------------------------ normalising

        /// <summary>
        /// Folds <c>allOf</c> into its parent where every branch is an ordinary object schema —
        /// which is what <c>allOf</c> nearly always is in the wild, since it is how a generator
        /// spells "this, plus a reference to that".
        /// </summary>
        /// <remarks>
        /// A deliberately approximate merge: where two branches state the same keyword, the first
        /// wins rather than the two being intersected. It cannot cause a wrong answer, because
        /// validation never sees this node — <see cref="SchemaValidator"/> runs against the original
        /// document (see the class remarks) — so the worst case is a box that looks more permissive
        /// than it is and a server that says no.
        /// </remarks>
        private JsonElement FlattenAllOf(JsonElement node, ref int pushed)
        {
            if (!node.TryGetProperty("allOf", out var branches) || branches.ValueKind != JsonValueKind.Array)
                return node;

            var resolved = new List<JsonElement>();

            foreach (var branch in branches.EnumerateArray())
            {
                var resolution = _resolver.Resolve(branch);
                if (!resolution.Ok) return node;

                pushed += resolution.PointersPushed;
                resolved.Add(resolution.Node);
            }

            if (resolved.Count == 0) return node;
            if (resolved.Count > 1 && !resolved.All(IsPlainObjectSchema)) return node;

            var merged = ToObject(node);
            merged.Remove("allOf");

            foreach (var branch in resolved) Overlay(merged, branch);

            return Reparse(merged);
        }

        /// <summary>
        /// <c>anyOf: [X, {"type": "null"}]</c> is how half the generators in the world spell "X, or
        /// nothing". Rendering that as a JSON editor because it is technically a union would be a
        /// large and needless loss: it is one field, and it may be left empty.
        /// </summary>
        private JsonElement UnwrapNullableUnion(JsonElement node, ref int pushed, ref bool allowsNull)
        {
            var keyword = node.TryGetProperty("anyOf", out var anyOf) ? anyOf
                        : node.TryGetProperty("oneOf", out var oneOf) ? oneOf
                        : default;

            if (keyword.ValueKind != JsonValueKind.Array) return node;

            var survivors = new List<JsonElement>();
            var nulls = 0;

            foreach (var branch in keyword.EnumerateArray())
            {
                var resolution = _resolver.Resolve(branch);
                if (!resolution.Ok) return node;

                pushed += resolution.PointersPushed;

                if (IsNullSchema(resolution.Node)) nulls++;
                else survivors.Add(resolution.Node);
            }

            // One real branch, and any number of null ones. A single-branch union with no null is
            // the same shape wearing a wrapper, and unwraps for the same reason.
            if (survivors.Count != 1) return node;

            var merged = ToObject(node);
            merged.Remove("anyOf");
            merged.Remove("oneOf");
            Overlay(merged, survivors[0]);

            allowsNull |= nulls > 0;
            return Reparse(merged);
        }

        // ------------------------------------------------------------------ pieces

        private FieldModel Field(
            string name, string path, JsonElement node, bool isRequired, FieldKind kind, FieldTier tier)
        {
            var title = Text(node, "title", TextSanitizer.NameLength);
            var description = RawText(node, "description");

            return new FieldModel
            {
                Name = name,
                Path = path,
                Label = title.Length > 0 ? title : LabelFor(name),
                Kind = kind,
                Tier = tier,
                HelpText = TextSanitizer.FirstSentence(description),
                FullDescription = TextSanitizer.CleanMultiline(description),
                IsRequired = isRequired,
                Constraints = ReadConstraints(node),
                Schema = node,
                Default = node.TryGetProperty("default", out var value) ? value : null,
                IsDeprecated = Flag(node, "deprecated"),
                IsReadOnly = Flag(node, "readOnly"),
            };
        }

        private FieldModel Degraded(
            string name, string path, JsonElement node, bool isRequired, string reason) =>
            Field(name, path, node, isRequired, FieldKind.Raw, FieldTier.Raw) with
            {
                DegradedReason = reason,
                HelpText = BuildRawHelp(node, reason),
            };

        /// <summary>
        /// A Tier 3 field's help text has one job the others do not: say what happened. §7.18 —
        /// at an edge, silence is indistinguishable from a bug.
        /// </summary>
        private static string BuildRawHelp(JsonElement node, string reason)
        {
            var description = TextSanitizer.FirstSentence(RawText(node, "description"));
            var explanation = $"Edit this value as JSON. The schema describes {reason}.";

            return description.Length > 0 ? $"{description} {explanation}" : explanation;
        }

        private FieldKind StringKind(JsonElement node)
        {
            var format = Text(node, "format", 60);

            if (format == "date") return FieldKind.Date;
            if (format == "date-time") return FieldKind.DateTime;

            if (Integer(node, "maxLength") is { } maxLength && maxLength > options.MultilineThreshold)
                return FieldKind.MultilineText;

            // A default with a line break in it is the schema telling us the box needs to be tall,
            // without a keyword for saying so.
            if (node.TryGetProperty("default", out var value)
                && value.ValueKind == JsonValueKind.String
                && (value.GetString() ?? "").Contains('\n'))
            {
                return FieldKind.MultilineText;
            }

            return FieldKind.Text;
        }

        private static FieldConstraints ReadConstraints(JsonElement node)
        {
            var constraints = new FieldConstraints
            {
                MinLength = Integer(node, "minLength"),
                MaxLength = Integer(node, "maxLength"),
                Pattern = Text(node, "pattern", 500) is { Length: > 0 } pattern ? pattern : null,
                Format = Text(node, "format", 60) is { Length: > 0 } format ? format : null,
                Minimum = Numeric(node, "minimum"),
                Maximum = Numeric(node, "maximum"),
                ExclusiveMinimum = Numeric(node, "exclusiveMinimum"),
                ExclusiveMaximum = Numeric(node, "exclusiveMaximum"),
                MultipleOf = Numeric(node, "multipleOf"),
                MinItems = Integer(node, "minItems"),
                MaxItems = Integer(node, "maxItems"),
                UniqueItems = Flag(node, "uniqueItems"),
            };

            return constraints.IsEmpty ? FieldConstraints.None : constraints;
        }

        /// <summary>
        /// The options from <c>enum</c> or <c>const</c>, or <c>null</c> when this node is not a
        /// closed set of values at all.
        /// </summary>
        private static List<ChoiceOption>? Enumeration(JsonElement node)
        {
            if (node.TryGetProperty("const", out var constant))
                return IsPrimitive(constant) ? [Option(constant, null)] : null;

            if (!node.TryGetProperty("enum", out var values) || values.ValueKind != JsonValueKind.Array)
                return null;

            var names = node.TryGetProperty("enumNames", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : null).ToList()
                : null;

            var options = new List<ChoiceOption>();
            var index = 0;

            foreach (var value in values.EnumerateArray())
            {
                // An enumeration of objects is a closed set whose members have no name worth
                // hearing. A dropdown of raw JSON is worse than an editor, so it is not one.
                if (!IsPrimitive(value)) return null;

                options.Add(Option(value, names is not null && index < names.Count ? names[index] : null));
                index++;
            }

            return options;
        }

        /// <summary>
        /// The options when a union is really an enumeration: every branch pinned to one value.
        /// <c>anyOf: [{const: "a", title: "Alpha"}, …]</c> is how a labelled enumeration is spelled
        /// once someone wants per-value descriptions, and it is a choice in every sense that matters.
        /// </summary>
        private List<ChoiceOption>? ConstantUnion(JsonElement node, ref bool allowsNull)
        {
            var keyword = node.TryGetProperty("anyOf", out var anyOf) ? anyOf
                        : node.TryGetProperty("oneOf", out var oneOf) ? oneOf
                        : default;

            if (keyword.ValueKind != JsonValueKind.Array) return null;
            if (node.TryGetProperty("allOf", out _) || HasConditional(node)) return null;

            var options = new List<ChoiceOption>();

            foreach (var branch in keyword.EnumerateArray())
            {
                var resolution = _resolver.Resolve(branch);
                try
                {
                    if (!resolution.Ok) return null;

                    var member = resolution.Node;

                    if (IsNullSchema(member))
                    {
                        allowsNull = true;
                        continue;
                    }

                    if (member.ValueKind != JsonValueKind.Object) return null;
                    if (HasComposition(member)) return null;

                    var single = Enumeration(member);
                    if (single is not { Count: 1 }) return null;

                    var label = Text(member, "title", TextSanitizer.NameLength);
                    options.Add(label.Length > 0 ? single[0] with { Label = label } : single[0]);
                }
                finally
                {
                    _resolver.Release(resolution.PointersPushed);
                }
            }

            return options.Count > 0 ? options : null;
        }

        /// <summary>
        /// A value's label when the schema offers none. Enum values are left exactly as they are —
        /// they are identifiers as often as they are words, and "us-east-1" humanised into "Us east
        /// 1" is a value nobody could type back.
        /// </summary>
        private static ChoiceOption Option(JsonElement value, string? name)
        {
            var option = new ChoiceOption("", value.Clone());
            var label = TextSanitizer.Clean(name, TextSanitizer.NameLength);

            return option with { Label = label.Length > 0 ? label : Describe(option.Value) };
        }

        private static string Describe(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => TextSanitizer.Clean(value.GetString(), TextSanitizer.NameLength),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Null => "None",
            _ => TextSanitizer.Clean(value.GetRawText(), TextSanitizer.NameLength),
        };

        /// <summary>
        /// A label is never empty, because §7.1 says a control's accessible name is never empty. The
        /// only nameless field is a list's element template, and "Item" is what a screen reader
        /// should say when the caret is inside one.
        /// </summary>
        private static string LabelFor(string name) => name.Length == 0
            ? "Item"
            : NameHumanizer.Humanize(name) is { Length: > 0 } humanised ? humanised : "Value";

        private static string CompositionReason(JsonElement node)
        {
            if (HasConditional(node)) return "arguments that change shape depending on other values";

            var keyword = node.TryGetProperty("oneOf", out var oneOf) ? oneOf
                        : node.TryGetProperty("anyOf", out var anyOf) ? anyOf
                        : node.TryGetProperty("allOf", out var allOf) ? allOf
                        : default;

            var count = keyword.ValueKind == JsonValueKind.Array ? keyword.GetArrayLength() : 0;

            return count > 1
                ? $"a choice between {count} different shapes"
                : "a combination of schemas this form cannot merge";
        }

        // ------------------------------------------------------------------ JSON odds and ends

        private static bool HasComposition(JsonElement node) =>
            node.TryGetProperty("oneOf", out _) || node.TryGetProperty("anyOf", out _)
            || node.TryGetProperty("allOf", out _) || HasConditional(node);

        private static bool HasConditional(JsonElement node) =>
            node.TryGetProperty("if", out _) || node.TryGetProperty("not", out _)
            || node.TryGetProperty("dependentSchemas", out _);

        private static bool IsPlainObjectSchema(JsonElement node) =>
            node.ValueKind == JsonValueKind.Object
            && !HasComposition(node)
            && (node.TryGetProperty("properties", out _) || TypeNames(node) is ["object"]);

        private static bool IsNullSchema(JsonElement node) =>
            node.ValueKind == JsonValueKind.Object
            && (TypeNames(node) is ["null"]
                || (node.TryGetProperty("const", out var constant) && constant.ValueKind == JsonValueKind.Null));

        private static bool IsPrimitive(JsonElement value) =>
            value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                            or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

        private static bool IsPrimitive(FieldKind kind) =>
            kind is FieldKind.Text or FieldKind.MultilineText or FieldKind.Choice
                 or FieldKind.Number or FieldKind.Integer or FieldKind.Boolean
                 or FieldKind.Date or FieldKind.DateTime;

        /// <summary>
        /// <c>type: ["string", "null"]</c> — the other half of what <see cref="TypeNames"/> drops.
        /// The two are separate because the null is not a type the control has to render; it is a
        /// fact about whether the box may be left empty, and something has to carry it.
        /// </summary>
        private static bool DeclaresNull(JsonElement node) =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Array
            && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "null");

        /// <summary>
        /// The declared types with <c>"null"</c> filtered out — <c>type: ["string", "null"]</c> is a
        /// string that may be omitted, not a union of two things to choose between.
        /// </summary>
        private static List<string> TypeNames(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return [];
            if (!node.TryGetProperty("type", out var type)) return [];

            return type.ValueKind switch
            {
                JsonValueKind.String => [type.GetString() ?? ""],
                JsonValueKind.Array =>
                [
                    .. type.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString() ?? "")
                        .Where(t => t is not "null" and { Length: > 0 }),
                ],
                _ => [],
            };
        }

        /// <summary>
        /// What a schema that forgot to say <c>type</c> must have meant. Common enough to be worth
        /// guessing at, and every guess here is one keyword away from certain.
        /// </summary>
        private static string? InferType(JsonElement node) => node switch
        {
            _ when node.TryGetProperty("properties", out _) => "object",
            _ when node.TryGetProperty("items", out _) || node.TryGetProperty("prefixItems", out _) => "array",
            _ when node.TryGetProperty("minLength", out _) || node.TryGetProperty("maxLength", out _)
                   || node.TryGetProperty("pattern", out _) || node.TryGetProperty("format", out _) => "string",
            _ when node.TryGetProperty("minimum", out _) || node.TryGetProperty("maximum", out _)
                   || node.TryGetProperty("multipleOf", out _) => "number",
            _ => null,
        };

        private static HashSet<string> RequiredNames(JsonElement node)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            if (node.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var name in required.EnumerateArray())
                    if (name.ValueKind == JsonValueKind.String && name.GetString() is { } text)
                        names.Add(text);

            return names;
        }

        private static string Text(JsonElement node, string property, int maxLength) =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? TextSanitizer.Clean(value.GetString(), maxLength)
                : "";

        /// <summary>Unsanitised, for the two callers that sanitise it themselves and differently.</summary>
        private static string? RawText(JsonElement node, string property) =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int? Integer(JsonElement node, string property) =>
            node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

        private static double? Numeric(JsonElement node, string property) =>
            node.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
                ? number
                : null;

        private static bool Flag(JsonElement node, string property) =>
            node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

        private static JsonObject ToObject(JsonElement node) =>
            JsonNode.Parse(node.GetRawText()) as JsonObject ?? [];

        private static void Overlay(JsonObject target, JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object) return;

            foreach (var member in source.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "properties" when target["properties"] is JsonObject existing:
                        foreach (var property in member.Value.EnumerateObject())
                            existing.TryAdd(property.Name, JsonNode.Parse(property.Value.GetRawText()));
                        break;

                    case "required" when target["required"] is JsonArray required:
                        foreach (var name in member.Value.EnumerateArray())
                            required.Add(JsonNode.Parse(name.GetRawText()));
                        break;

                    default:
                        target.TryAdd(member.Name, JsonNode.Parse(member.Value.GetRawText()));
                        break;
                }
            }
        }

        private static JsonElement Reparse(JsonObject node) =>
            JsonDocument.Parse(node.ToJsonString()).RootElement;

        private static string Join(List<string> values) => values.Count switch
        {
            0 => "nothing",
            1 => values[0],
            _ => string.Join(", ", values[..^1]) + " or " + values[^1],
        };
    }
}

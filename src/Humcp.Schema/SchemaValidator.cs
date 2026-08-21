using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Humcp.Schema;

/// <summary>
/// Checks a value against the schema it came from, and says what is wrong in words meant to be
/// heard (ROADMAP §5, §7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Assertion semantics come from JsonSchema.Net; every sentence comes from here.</b> Getting
/// 2020-12 right is a conformance job, and a subset written by hand would disagree with the server
/// about what is valid — which is the one thing a client must not do, because it would refuse calls
/// the server would have accepted. The wording is the opposite kind of job: these strings are read
/// aloud to someone who cannot see which box is red, so <c>Required properties ["owner"] were not
/// present</c> is not good enough.
/// </para>
/// <para>
/// <b>It validates the original schema, never the builder's simplified one.</b>
/// <see cref="FormBuilder"/> merges <c>allOf</c> and unwraps nullable unions to decide what controls
/// to draw; those simplifications must not be able to change what counts as valid.
/// </para>
/// <para>
/// <b>It never reaches the network.</b> A schema is a third-party document (§4, pitfall 5), and a
/// <c>$ref</c> to <c>https://attacker.example/</c> would otherwise turn opening a form into an
/// outbound request to an address the server chose. The registry's fetch hook is pinned shut.
/// </para>
/// </remarks>
public static class SchemaValidator
{
    /// <summary>Cap on how many problems are reported at once. A wall of errors helps nobody.</summary>
    public const int MaxIssues = 50;

    /// <summary>Checks a value, naming fields by their labels where <paramref name="form"/> knows them.</summary>
    public static ValidationReport Validate(FormModel form, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(form);

        return Validate(form.Schema, value, form);
    }

    /// <summary>
    /// Checks the text in a Tier 3 editor. Invalid JSON is itself the first thing to report — and
    /// it is reported as one plain sentence, not as a parser's idea of a diagnostic.
    /// </summary>
    public static ValidationReport ValidateJson(JsonElement schema, string? json, FormModel? form = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return Validate(schema, null, form);

        JsonNode? value;

        try
        {
            value = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new ValidationReport
            {
                Issues =
                [
                    new ValidationIssue("", "", "",
                        $"This is not valid JSON. {TextSanitizer.Clean(ex.Message, 200)}", "json"),
                ],
            };
        }

        return Validate(schema, value, form);
    }

    /// <summary>Checks a value against a schema, with no form to take labels from.</summary>
    public static ValidationReport Validate(JsonElement schema, JsonNode? value, FormModel? form = null)
    {
        if (schema.ValueKind != JsonValueKind.Object && schema.ValueKind != JsonValueKind.True
            && schema.ValueKind != JsonValueKind.False)
        {
            return ValidationReport.Unchecked("This tool did not describe its arguments, so nothing was checked.");
        }

        EvaluationResults results;

        try
        {
            var compiled = JsonSchema.Build(schema, Building);
            results = compiled.Evaluate(AsElement(value), Evaluating);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A schema the validator cannot even load is a fact about the server, not about the
            // user's input, and it is never a reason to stop them calling the tool.
            return ValidationReport.Unchecked(
                $"These arguments could not be checked here — the server's schema was rejected: " +
                $"{TextSanitizer.Clean(ex.Message, 200)}");
        }

        if (results.IsValid) return ValidationReport.Clean;

        return new ValidationReport { Issues = Collect(results, schema, value, form) };
    }

    /// <summary>
    /// 2020-12 explicitly, because that is the dialect the MCP spec pins <c>inputSchema</c> to, and
    /// because most real schemas never say <c>$schema</c> at all — without this they would be read
    /// as whatever the library's default happens to be in the version we upgrade to next.
    /// </summary>
    /// <remarks>
    /// The registry is our own instance rather than <c>SchemaRegistry.Global</c>, which is what
    /// <c>BuildOptions</c> uses by default: pinning the fetch hook shut is a decision about this
    /// validator, and reaching into a shared static to make it would be making it for everybody.
    /// </remarks>
    private static readonly BuildOptions Building = new()
    {
        Dialect = Dialect.Draft202012,

        // Returning null turns an unfetchable reference into an error we catch, and makes it
        // structurally impossible for opening a form to open a socket. See the class remarks.
        SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null },
    };

    private static readonly EvaluationOptions Evaluating = new()
    {
        OutputFormat = OutputFormat.List,

        // Without this, every failure deep in a form also surfaces as "some properties did not
        // match" against the object above it, and again against the object above that. The user
        // hears the same problem three times and only the innermost one names the field.
        IncludeApplicatorErrors = false,

        // 2020-12 makes `format` an annotation rather than an assertion. Ours asserts: a malformed
        // email is worth saying now rather than after a round trip — and it is a warning either
        // way, never a block.
        RequireFormatValidation = true,
    };

    private static JsonElement AsElement(JsonNode? value) =>
        value is null ? NullElement : JsonSerializer.SerializeToElement(value);

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement;

    // ------------------------------------------------------------------ results → sentences

    private static List<ValidationIssue> Collect(
        EvaluationResults results, JsonElement schema, JsonNode? value, FormModel? form)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var detail in Flatten(results))
        {
            if (detail.Errors is not { Count: > 0 } errors) continue;


            foreach (var (keyword, message) in errors)
            {
                foreach (var issue in Describe(detail, keyword, message, schema, value, form))
                {
                    // Escaped rather than typed: a raw NUL in a source file makes git treat the
                    // whole file as binary, and an undiffable source file is a bad trade for a
                    // separator. It is still the right separator — no message can contain one.
                    if (seen.Add($"{issue.FieldPath}\0{issue.Keyword}\0{issue.Message}"))
                        issues.Add(issue);

                    if (issues.Count >= MaxIssues) return issues;
                }
            }
        }

        // Shallow problems first: a missing required field at the top is more use to hear than the
        // pattern failure three levels inside the thing it is missing from.
        return [.. issues.OrderBy(i => i.InstancePath.Count(c => c is '.' or '['))
                         .ThenBy(i => i.InstancePath, StringComparer.Ordinal)];
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (var detail in results.Details ?? [])
            foreach (var nested in Flatten(detail))
                yield return nested;
    }

    private static IEnumerable<ValidationIssue> Describe(
        EvaluationResults detail, string keyword, string message,
        JsonElement schema, JsonNode? value, FormModel? form)
    {
        var instance = Path(detail.InstanceLocation.ToString());
        var field = FieldFor(form, Normalise(instance));
        var constraint = ConstraintFor(field, detail, keyword, schema);

        // `required` reports against the object that is missing something, so on its own it points
        // at the group rather than at the empty box. Split it into one issue per missing name, each
        // landing on the field the user has to go and fill in (§7.8).
        if (keyword is "required" && constraint is { ValueKind: JsonValueKind.Array } names)
        {
            var present = Locate(value, detail.InstanceLocation.ToString()) as JsonObject;

            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } property) continue;
                if (present is not null && present.ContainsKey(property)) continue;

                var path = instance.Length == 0 ? property : $"{instance}.{property}";
                yield return Issue(path, form, "This one is required.", keyword);
            }

            yield break;
        }

        yield return Issue(instance, form, Sentence(keyword, constraint, message), keyword);
    }

    private static ValidationIssue Issue(string instancePath, FormModel? form, string message, string keyword)
    {
        var fieldPath = Normalise(instancePath);

        return new ValidationIssue(
            FieldPath: fieldPath,
            InstancePath: instancePath,
            Label: LabelFor(instancePath, fieldPath, form),
            Message: message,
            Keyword: keyword);
    }

    /// <summary>
    /// What the error summary calls this. An entry inside a list is named by the list and its
    /// position — "Tags, entry 2" — because "Item" is what every row of every list is called and
    /// tells the listener nothing about where to go.
    /// </summary>
    private static string LabelFor(string instancePath, string fieldPath, FormModel? form)
    {
        if (!instancePath.EndsWith(']')) return FieldFor(form, fieldPath)?.Label ?? LastSegment(instancePath);

        var open = instancePath.LastIndexOf('[');
        var owner = instancePath[..open];
        var position = instancePath[(open + 1)..^1];

        var name = FieldFor(form, Normalise(owner))?.Label ?? LastSegment(owner);

        return int.TryParse(position, CultureInfo.InvariantCulture, out var index)
            ? $"{name}, entry {index + 1}"
            : name;
    }

    private static FieldModel? FieldFor(FormModel? form, string fieldPath) =>
        form?.AllFields().FirstOrDefault(f => f.Path == fieldPath);

    /// <summary>
    /// The keyword's own value, so a message can quote it. The field's resolved schema is asked
    /// first and answers nearly always: <see cref="FormBuilder"/> has already followed the
    /// <c>$ref</c>s to get there, and its answer needs no pointer arithmetic to be right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second route exists for the fields there is no model for — a Tier 3 editor holding a
    /// whole object, or a call made with no <see cref="FormModel"/> at all — and walks the schema
    /// alongside the instance path instead.
    /// </para>
    /// <para>
    /// Deliberately not the library's own <c>SchemaLocation</c> or <c>EvaluationPath</c>: in 9.4.0
    /// both come back with a duplicated final segment for a value inside an array
    /// (<c>/properties/tags/items/items</c>), so a pointer built from either resolves to nothing.
    /// Nothing is broken by that — it is only a route to a nicer sentence — but it is not a route
    /// worth taking.
    /// </para>
    /// </remarks>
    private static JsonElement? ConstraintFor(
        FieldModel? field, EvaluationResults detail, string keyword, JsonElement schema)
    {
        if (field is { Schema.ValueKind: JsonValueKind.Object }
            && field.Schema.TryGetProperty(keyword, out var stated))
        {
            return stated;
        }

        return SchemaForInstance(schema, detail.InstanceLocation.ToString()) is
                   { ValueKind: JsonValueKind.Object } node
               && node.TryGetProperty(keyword, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// The schema node governing one place in the instance: object keys step through
    /// <c>properties</c>, list positions step through <c>items</c>, and <c>$ref</c> is followed at
    /// every step by the same resolver the form builder uses.
    /// </summary>
    private static JsonElement? SchemaForInstance(JsonElement schema, string pointer)
    {
        var resolver = new SchemaResolver(schema);

        var resolution = resolver.Resolve(schema);
        if (!resolution.Ok) return null;

        var current = resolution.Node;

        foreach (var raw in pointer.Split('/').Skip(1))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;

            var token = raw.Replace("~1", "/", StringComparison.Ordinal)
                           .Replace("~0", "~", StringComparison.Ordinal);

            JsonElement next;

            if (int.TryParse(token, CultureInfo.InvariantCulture, out _))
            {
                if (!current.TryGetProperty("items", out next)) return null;
            }
            else if (current.TryGetProperty("properties", out var properties)
                     && properties.ValueKind == JsonValueKind.Object
                     && properties.TryGetProperty(token, out var member))
            {
                next = member;
            }
            else
            {
                return null;
            }

            // No Release: this walk only ever goes downwards, so the resolver's active set is
            // exactly the path we are on, which is what makes a cycle here a genuine one.
            resolution = resolver.Resolve(next);
            if (!resolution.Ok) return null;

            current = resolution.Node;
        }

        return current;
    }

    /// <summary>
    /// Our own wording for the keywords a person actually trips over. Anything else falls back to
    /// the library's sentence, cleaned — better a slightly stilted true statement than a made-up one.
    /// </summary>
    private static string Sentence(string keyword, JsonElement? constraint, string libraryMessage) => keyword switch
    {
        "type" => $"This should be {Words(constraint)}.",
        "enum" => $"This has to be one of: {List(constraint)}.",
        "const" => $"This has to be {Value(constraint)}.",

        "minLength" => $"This needs at least {Count(constraint, "character")}.",
        "maxLength" => $"This can be at most {Count(constraint, "character")}.",
        "pattern" => "This is not in the form the server expects.",
        "format" => $"This should be {FormatWords(constraint)}.",

        "minimum" => $"This has to be {Number(constraint)} or more.",
        "maximum" => $"This has to be {Number(constraint)} or less.",
        "exclusiveMinimum" => $"This has to be more than {Number(constraint)}.",
        "exclusiveMaximum" => $"This has to be less than {Number(constraint)}.",
        "multipleOf" => $"This has to be a multiple of {Number(constraint)}.",

        "minItems" => $"This needs at least {Count(constraint, "item")}.",
        "maxItems" => $"This can hold at most {Count(constraint, "item")}.",
        "uniqueItems" => "The entries in this list have to be different from each other.",

        "additionalProperties" or "unevaluatedProperties" =>
            "This has a name the server does not recognise.",
        "propertyNames" => "One of the names in here is not one the server accepts.",
        "dependentRequired" => "Filling this one in means another one has to be filled in too.",

        "oneOf" => "This does not match any one of the shapes the server accepts here.",
        "anyOf" => "This does not match any of the shapes the server accepts here.",
        "allOf" => "This does not meet all of the rules the server sets here.",
        "not" => "This is a value the server specifically excludes.",

        _ => TextSanitizer.Clean(libraryMessage, 200) is { Length: > 0 } cleaned
            ? EndWithStop(cleaned)
            : "The server would not accept this value.",
    };

    // ------------------------------------------------------------------ small helpers

    /// <summary>A JSON pointer as one of our field paths: <c>/a/b/2</c> becomes <c>a.b[2]</c>.</summary>
    private static string Path(string pointer)
    {
        if (pointer.Length is 0 or 1) return "";

        var sb = new StringBuilder();

        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal)
                           .Replace("~0", "~", StringComparison.Ordinal);

            if (int.TryParse(token, CultureInfo.InvariantCulture, out var index)) sb.Append('[').Append(index).Append(']');
            else if (sb.Length == 0) sb.Append(token);
            else sb.Append('.').Append(token);
        }

        return sb.ToString();
    }

    /// <summary><c>tags[2]</c> becomes <c>tags[]</c>, which is how <see cref="FieldModel.Path"/> spells it.</summary>
    private static string Normalise(string path)
    {
        var sb = new StringBuilder(path.Length);
        var inIndex = false;

        foreach (var c in path)
        {
            if (c == '[') { inIndex = true; sb.Append('['); }
            else if (c == ']') { inIndex = false; sb.Append(']'); }
            else if (!inIndex) sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The field's name when there is no <see cref="FormModel"/> to take a label from — humanised,
    /// because a raw property name is a machine's spelling and this one is going to be spoken
    /// (§7.1). Falls back to the path itself where there is nothing to humanise.
    /// </summary>
    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd(']');
        var cut = trimmed.LastIndexOfAny(['.', '[']);
        var segment = cut < 0 ? trimmed : trimmed[(cut + 1)..];

        return NameHumanizer.Humanize(segment) is { Length: > 0 } humanised ? humanised : segment;
    }

    private static JsonNode? Locate(JsonNode? value, string pointer)
    {
        if (value is null || pointer.Length is 0 or 1) return value;

        var current = value;

        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal)
                           .Replace("~0", "~", StringComparison.Ordinal);

            current = current switch
            {
                JsonObject o when o.TryGetPropertyValue(token, out var member) => member,
                JsonArray a when int.TryParse(token, CultureInfo.InvariantCulture, out var i)
                                 && i >= 0 && i < a.Count => a[i],
                _ => null,
            };

            if (current is null) return null;
        }

        return current;
    }

    private static string Words(JsonElement? constraint) => constraint switch
    {
        { ValueKind: JsonValueKind.String } single => TypeWord(single.GetString()),
        { ValueKind: JsonValueKind.Array } many =>
            Join([.. many.EnumerateArray().Select(t => TypeWord(t.GetString()))]),
        _ => "a different kind of value",
    };

    /// <summary>
    /// Type names as they would be said out loud, article included. "String" and "integer" are
    /// words about JSON, and the person hearing this is filling in a form, not reading a spec.
    /// </summary>
    private static string TypeWord(string? type) => type switch
    {
        "string" => "text",
        "integer" => "a whole number",
        "number" => "a number",
        "boolean" => "yes or no",
        "array" => "a list",
        "object" => "a group of values",
        "null" => "nothing",
        _ => "a different kind of value",
    };

    private static string FormatWords(JsonElement? constraint) =>
        constraint is { ValueKind: JsonValueKind.String } format
            ? new FieldConstraints { Format = format.GetString() }.Describe().TrimEnd('.') is { Length: > 0 } described
                ? described[0].ToString().ToLower(CultureInfo.CurrentCulture) + described[1..]
                : $"in {format.GetString()} form"
            : "in the form the server expects";

    private static string List(JsonElement? constraint)
    {
        if (constraint is not { ValueKind: JsonValueKind.Array } values) return "one of the listed values";

        var options = values.EnumerateArray().Take(10).Select(Text).ToList();
        var suffix = values.GetArrayLength() > 10 ? ", or one of the others" : "";

        return Join(options) + suffix;
    }

    private static string Value(JsonElement? constraint) =>
        constraint is { } value ? Text(value) : "the value the server specifies";

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => TextSanitizer.Clean(value.GetString(), 60),
        JsonValueKind.Null => "nothing",
        _ => TextSanitizer.Clean(value.GetRawText(), 60),
    };

    private static string Count(JsonElement? constraint, string noun)
    {
        var count = constraint is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var n) ? n : 0;

        return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    private static string Number(JsonElement? constraint) =>
        constraint is { ValueKind: JsonValueKind.Number } value ? value.GetRawText() : "the stated limit";

    private static string Join(List<string> values) => values.Count switch
    {
        0 => "",
        1 => values[0],
        _ => string.Join(", ", values[..^1]) + " or " + values[^1],
    };

    private static string EndWithStop(string text) =>
        text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
}

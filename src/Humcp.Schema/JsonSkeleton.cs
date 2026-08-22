using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humcp.Schema;

/// <summary>
/// Builds the starting text for a Tier 3 editor: the shape the schema describes, with the values
/// left blank (ROADMAP §5).
/// </summary>
/// <remarks>
/// <para>
/// Tier 3 is the tier that exists so that no tool is ever a dead end, and "here is a box, type JSON
/// into it" is a dead end for anyone who cannot see the schema. What the box starts with is the
/// difference between being told there is a shape and being shown it — the field names spelled
/// correctly, the nesting already right, and a caret that can be arrowed through them.
/// </para>
/// <para>
/// Best effort by design. It reads <c>default</c>, <c>const</c>, <c>examples</c> and <c>enum</c>
/// before falling back to the type, takes the first branch of a union, and gives up quietly at
/// depth or on anything it cannot read — an empty box is a fine place to start and a wrong shape
/// is not. Nothing downstream depends on it: the text is the user's from the moment the window
/// opens, and <see cref="SchemaValidator"/> judges whatever they leave there.
/// </para>
/// </remarks>
public static class JsonSkeleton
{
    private const int DefaultMaxDepth = 4;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The skeleton as text, or <c>""</c> when the schema says nothing worth showing.</summary>
    public static string For(JsonElement schema, int maxDepth = DefaultMaxDepth)
    {
        var resolver = new SchemaResolver(schema);
        var node = Build(schema, resolver, maxDepth);

        return node is null ? "" : node.ToJsonString(Indented);
    }

    /// <summary>One JSON value, indented the way the editor shows it.</summary>
    public static string Format(JsonElement value)
    {
        try
        {
            return JsonSerializer.Serialize(value, Indented);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "";
        }
    }

    private static JsonNode? Build(JsonElement schema, SchemaResolver resolver, int depth)
    {
        if (depth < 0) return null;

        var resolution = resolver.Resolve(schema);
        if (!resolution.Ok) return null;

        try
        {
            var node = resolution.Node;

            if (node.ValueKind != JsonValueKind.Object) return null;

            if (Stated(node) is { } stated) return Copy(stated);
            if (Branch(node) is { } branch) return Build(branch, resolver, depth - 1);

            return ByType(node, resolver, depth);
        }
        finally
        {
            resolver.Release(resolution.PointersPushed);
        }
    }

    /// <summary>A value the schema itself puts forward, which beats anything invented here.</summary>
    private static JsonElement? Stated(JsonElement node)
    {
        if (node.TryGetProperty("default", out var stated)) return stated;
        if (node.TryGetProperty("const", out var constant)) return constant;

        if (node.TryGetProperty("examples", out var examples)
            && examples.ValueKind == JsonValueKind.Array
            && examples.GetArrayLength() > 0)
        {
            return examples[0];
        }

        if (node.TryGetProperty("enum", out var values)
            && values.ValueKind == JsonValueKind.Array
            && values.GetArrayLength() > 0)
        {
            return values[0];
        }

        return null;
    }

    /// <summary>
    /// The branch of a union to show. The first one, because there is no way to ask which the user
    /// wants before they have seen any of them, and one shape they can edit beats none.
    /// </summary>
    private static JsonElement? Branch(JsonElement node)
    {
        foreach (var keyword in (string[])["oneOf", "anyOf", "allOf"])
        {
            if (!node.TryGetProperty(keyword, out var branches)) continue;
            if (branches.ValueKind != JsonValueKind.Array) continue;

            foreach (var branch in branches.EnumerateArray())
            {
                // A null branch is how "or nothing" is spelled, and nothing is not a shape to show.
                if (branch.ValueKind == JsonValueKind.Object
                    && branch.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "null")
                {
                    continue;
                }

                return branch;
            }
        }

        return null;
    }

    private static JsonNode? ByType(JsonElement node, SchemaResolver resolver, int depth) =>
        TypeName(node) switch
        {
            "object" => Object(node, resolver, depth),
            "array" => Array(node, resolver, depth),
            "string" => JsonValue.Create(""),
            "integer" or "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            _ => null,
        };

    private static JsonNode Object(JsonElement node, SchemaResolver resolver, int depth)
    {
        var value = new JsonObject();

        if (!node.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            // "Any keys at all, none of them named" — 17 of the corpus's 39 Tier 3 parameters. An
            // empty object is exactly right: it is the shape, and every key in it is the user's.
            return value;
        }

        foreach (var property in properties.EnumerateObject())
            value[property.Name] = Build(property.Value, resolver, depth - 1);

        return value;
    }

    private static JsonNode Array(JsonElement node, SchemaResolver resolver, int depth)
    {
        var value = new JsonArray();

        if (node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            value.Add(Build(items, resolver, depth - 1));

        return value;
    }

    private static string? TypeName(JsonElement node)
    {
        if (!node.TryGetProperty("type", out var type)) return Infer(node);

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            JsonValueKind.Array => type.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString())
                .FirstOrDefault(t => t is not "null"),
            _ => Infer(node),
        };
    }

    private static string? Infer(JsonElement node) =>
        node.TryGetProperty("properties", out _) ? "object"
        : node.TryGetProperty("items", out _) ? "array"
        : null;

    private static JsonNode? Copy(JsonElement value)
    {
        try
        {
            return JsonNode.Parse(value.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

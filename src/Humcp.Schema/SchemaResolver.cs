using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humcp.Schema;

/// <summary>Why a <c>$ref</c> did not become a schema node.</summary>
internal enum ResolutionStatus
{
    /// <summary>There was nothing to resolve, or it resolved.</summary>
    Resolved,

    /// <summary>The reference points back at something already being expanded on this branch.</summary>
    Cycle,

    /// <summary>The reference points somewhere this resolver will not follow.</summary>
    Unresolvable,
}

/// <summary>The outcome of following a <c>$ref</c> chain to its end.</summary>
internal readonly record struct Resolution(
    ResolutionStatus Status,
    JsonElement Node,
    int PointersPushed,
    string? Reason)
{
    public bool Ok => Status == ResolutionStatus.Resolved;
}

/// <summary>
/// Follows <c>$ref</c> chains within one schema document (ROADMAP §5, Tier 2), and refuses to follow
/// them out of it.
/// </summary>
/// <remarks>
/// <para>
/// Two rules do the work. <b>Local only:</b> a reference to <c>https://example.com/schema.json</c> is
/// reported unresolvable rather than fetched — the schema came from a third party (§4, pitfall 5),
/// and turning a tool listing into an outbound request to an address that tool listing chose is not
/// something a client should do quietly.
/// </para>
/// <para>
/// <b>Cycles demote, they do not throw.</b> A schema whose node refers to itself is legal, common in
/// tree-shaped data, and not renderable as a form of finite depth. The node becomes Tier 3, which is
/// what Tier 3 is for.
/// </para>
/// </remarks>
internal sealed class SchemaResolver
{
    private readonly JsonElement _root;

    /// <summary>Pointers currently being expanded, innermost last. A repeat is a cycle.</summary>
    private readonly List<string> _active = [];

    internal SchemaResolver(JsonElement root) => _root = root;

    /// <summary>
    /// Expands <paramref name="node"/> until it is not a reference. On success the caller owns
    /// <see cref="Resolution.PointersPushed"/> entries on the active stack and must
    /// <see cref="Release"/> them once it has finished with the node's children.
    /// </summary>
    internal Resolution Resolve(JsonElement node)
    {
        var pushed = 0;

        while (node.ValueKind == JsonValueKind.Object
               && node.TryGetProperty("$ref", out var reference)
               && reference.ValueKind == JsonValueKind.String)
        {
            var text = reference.GetString() ?? "";

            if (ParsePointer(text) is not { } pointer)
            {
                Release(pushed);
                return new Resolution(ResolutionStatus.Unresolvable, node, 0, ReasonFor(text));
            }

            if (_active.Contains(pointer, StringComparer.Ordinal))
            {
                Release(pushed);
                return new Resolution(ResolutionStatus.Cycle, node, 0, "a schema that refers to itself");
            }

            if (Follow(pointer) is not { } target)
            {
                Release(pushed);
                return new Resolution(ResolutionStatus.Unresolvable, node, 0,
                    $"a reference to {TextSanitizer.Clean(text, 80)}, which this schema does not define");
            }

            _active.Add(pointer);
            pushed++;

            // 2020-12 permits keywords beside $ref, unlike the draft most servers were written
            // against. Where they exist the local ones win: a $ref plus its own description is a
            // real and frequent shape, and the local description is the one about this field.
            node = MergeSiblings(target, node);
        }

        return new Resolution(ResolutionStatus.Resolved, node, pushed, null);
    }

    /// <summary>Pops <paramref name="count"/> entries pushed by a matching <see cref="Resolve"/>.</summary>
    internal void Release(int count)
    {
        for (var i = 0; i < count && _active.Count > 0; i++) _active.RemoveAt(_active.Count - 1);
    }

    /// <summary>
    /// The fragment of a same-document reference as a JSON pointer, or <c>null</c> when this
    /// resolver will not follow it. <c>#</c> is the root and resolves to the empty pointer.
    /// </summary>
    private static string? ParsePointer(string reference)
    {
        if (reference.Length == 0) return null;
        if (reference == "#") return "";
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;

        return Uri.UnescapeDataString(reference[1..]);
    }

    private static string ReasonFor(string reference) => reference.StartsWith('#')
        // A plain-name fragment: "#node" is an $anchor, which needs the full $id machinery.
        ? $"a named anchor, {TextSanitizer.Clean(reference, 80)}"
        : $"an external reference to {TextSanitizer.Clean(reference, 80)}";

    private JsonElement? Follow(string pointer)
    {
        var current = _root;
        if (pointer.Length == 0) return current;

        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal)
                           .Replace("~0", "~", StringComparison.Ordinal);

            switch (current.ValueKind)
            {
                case JsonValueKind.Object when current.TryGetProperty(token, out var member):
                    current = member;
                    break;

                case JsonValueKind.Array when int.TryParse(token, out var index)
                                              && index >= 0 && index < current.GetArrayLength():
                    current = current[index];
                    break;

                default:
                    return null;
            }
        }

        return current;
    }

    /// <summary>
    /// <paramref name="target"/> overlaid with everything <paramref name="source"/> says besides
    /// <c>$ref</c>. Returns the target untouched in the common case where it says nothing else.
    /// </summary>
    private static JsonElement MergeSiblings(JsonElement target, JsonElement source)
    {
        var siblings = source.EnumerateObject()
            .Where(p => p.Name is not "$ref")
            .ToList();

        if (siblings.Count == 0) return target;
        if (target.ValueKind != JsonValueKind.Object) return target;

        var merged = JsonNode.Parse(target.GetRawText()) as JsonObject ?? [];
        foreach (var sibling in siblings)
            merged[sibling.Name] = JsonNode.Parse(sibling.Value.GetRawText());

        // Parsed into a detached document of its own: nothing else holds it, so it lives as long as
        // the FieldModel that ends up pointing at it and no longer.
        return JsonDocument.Parse(merged.ToJsonString()).RootElement;
    }
}

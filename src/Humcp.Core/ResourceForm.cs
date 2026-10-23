using System.Text.Json;
using System.Text.RegularExpressions;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>One variable of a URI template, and whether the address can be made without it.</summary>
public sealed record TemplateVariable(string Name, bool Required);

/// <summary>
/// Resources and resource templates as operations with a form (ROADMAP §11, Phase 13).
/// </summary>
/// <remarks>
/// <para>
/// A resource at a fixed address has nothing to fill in: it is an operation with an empty form,
/// read straight from the tree the way a tool with no parameters is run.
/// </para>
/// <para>
/// A template publishes no schema, only an RFC 6570 pattern — <c>file:///{path}</c>,
/// <c>repo://{owner}/{name}/issues{?state}</c>. Its variables are written out as the object of
/// strings they mean and handed to <see cref="FormBuilder"/>, exactly as a prompt's arguments are
/// (<see cref="PromptForm"/>), and the address is made from the answers by the SDK, which implements
/// the expansion; humcp only has to know the names. A variable in a query expression
/// (<c>{?…}</c>, <c>{&amp;…}</c>) is optional, because leaving it out still makes an address; every
/// other one is required, because leaving it out makes a different address than the one meant.
/// </para>
/// </remarks>
public static partial class ResourceForm
{
    /// <summary>The variables of a URI template, in order, each once.</summary>
    public static IReadOnlyList<TemplateVariable> Variables(string? template)
    {
        var variables = new List<TemplateVariable>();
        if (string.IsNullOrEmpty(template)) return variables;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match expression in Expression().Matches(template))
        {
            var optional = expression.Groups["op"].Value is "?" or "&";

            foreach (var spec in expression.Groups["vars"].Value.Split(','))
            {
                // A prefix (":3") or explode ("*") modifier changes how a value is written into the
                // address, not what the variable is called.
                var name = spec.Trim().TrimEnd('*');
                var colon = name.IndexOf(':');
                if (colon >= 0) name = name[..colon];

                if (name.Length == 0 || !seen.Add(name)) continue;
                variables.Add(new TemplateVariable(name, !optional));
            }
        }

        return variables;
    }

    /// <summary>A resource at a fixed address.</summary>
    public static ToolInfo Resource(string uri, string? name, string? title, string? description, string? mimeType)
        => new(
            Name: TextSanitizer.Clean(uri, TextSanitizer.NameLength),
            DisplayName: DisplayName(title, name, uri),
            Summary: TextSanitizer.FirstSentence(description),
            FullDescription: TextSanitizer.CleanMultiline(description),
            // Reading changes nothing, and saying so on every resource would be noise (as for prompts).
            Safety: OperationSafety.Unstated,
            Form: FormBuilder.Build(PromptForm.Schema([])))
        {
            Kind = OperationKind.Resource,
            ResourceUri = uri,
            MimeType = TextSanitizer.Clean(mimeType, 60),
        };

    /// <summary>A resource template, whose variables are its form.</summary>
    public static ToolInfo Template(string uriTemplate, string? name, string? title, string? description, string? mimeType)
    {
        var variables = Variables(uriTemplate);

        // The protocol gives a template's variables no descriptions, so each field would say nothing
        // but how to open its drop-down — found by ear on server-everything, whose template says
        // only in its own description that the one variable must be a number (phase13-notes). So
        // each says which address it is part of, and an only variable carries the template's own
        // first sentence, since everything the template says is about it.
        var summary = TextSanitizer.FirstSentence(description);
        var arguments = variables.Select(v => new PromptArgumentInfo(v.Name, null,
            (variables.Count == 1 && summary.Length > 0 ? summary + " " : "") + $"Part of the address {uriTemplate}.",
            v.Required));

        return new ToolInfo(
            Name: TextSanitizer.Clean(uriTemplate, TextSanitizer.NameLength),
            DisplayName: DisplayName(title, name, uriTemplate),
            Summary: TextSanitizer.FirstSentence(description),
            FullDescription: TextSanitizer.CleanMultiline(description),
            Safety: OperationSafety.Unstated,
            Form: FormBuilder.Build(PromptForm.Schema(arguments)))
        {
            Kind = OperationKind.ResourceTemplate,
            ResourceUri = uriTemplate,
            MimeType = TextSanitizer.Clean(mimeType, 60),
        };
    }

    /// <summary>
    /// Whether a tool's parameter wants an address, so the server's resources are worth offering
    /// in its drop-down (§4, pitfall 3's second half). Conservative on purpose: a parameter called
    /// <c>url</c> usually wants a web address, which a resource list would only clutter.
    /// </summary>
    public static bool WantsResourceUri(FieldModel field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Kind != FieldKind.Text) return false;
        if (field.Constraints.Format is "uri" or "uri-reference" or "iri" or "iri-reference") return true;

        var name = field.Name;
        return name.Equals("uri", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Uri", StringComparison.Ordinal)
               || name.EndsWith("_uri", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("-uri", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The title, or else the name exactly as written, or else the address. Not humanised as a
    /// tool's name is: a resource's name is usually a file name or an address, and "readme.md" read
    /// as "Readme md" is a different file.
    /// </summary>
    private static string DisplayName(string? title, string? name, string address)
    {
        foreach (var candidate in new[] { title, name, address })
        {
            var cleaned = TextSanitizer.Clean(candidate, TextSanitizer.NameLength);
            if (cleaned.Length > 0) return cleaned;
        }

        return "Unnamed resource";
    }

    [GeneratedRegex(@"\{(?<op>[+#./;?&]?)(?<vars>[^{}]+)\}")]
    private static partial Regex Expression();
}

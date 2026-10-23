using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>One argument a prompt takes, as the server listed it. Not yet sanitised.</summary>
public sealed record PromptArgumentInfo(string Name, string? Title, string? Description, bool Required);

/// <summary>
/// A prompt as an operation with a form (ROADMAP §11, Phase 12).
/// </summary>
/// <remarks>
/// <para>
/// A prompt publishes no schema: its arguments are a list of names, each with an optional title and
/// description and a required flag, and every value is a string. So the list is written out as the
/// JSON Schema it means — an object of string properties — and handed to <see cref="FormBuilder"/>
/// like any tool's. Everything a tool form has follows from that without a line of prompt-specific
/// code: labels, help, access keys, the optional toggle, validation of required fields, and the
/// omission rule for a box left empty.
/// </para>
/// <para>
/// Pure and in Core rather than in the connection, so what a prompt's form is can be argued with in
/// a test without a server.
/// </para>
/// </remarks>
public static class PromptForm
{
    /// <summary>The object schema a prompt's argument list means.</summary>
    public static JsonElement Schema(IEnumerable<PromptArgumentInfo> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var argument in arguments)
        {
            // A name is the key it is sent under; one missing or repeated cannot be sent at all, so
            // it is not drawn. The first of a repeated name wins, as it would in a JSON object.
            if (string.IsNullOrEmpty(argument.Name) || properties.ContainsKey(argument.Name)) continue;

            var property = new JsonObject { ["type"] = "string" };
            if (!string.IsNullOrWhiteSpace(argument.Title)) property["title"] = argument.Title;
            if (!string.IsNullOrWhiteSpace(argument.Description)) property["description"] = argument.Description;

            properties[argument.Name] = property;
            if (argument.Required) required.Add(argument.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;

        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// The prompt as an operation the form window and the tree already understand. Sanitised here,
    /// at the edge, like a tool (§4, pitfall 5).
    /// </summary>
    public static ToolInfo Build(string name, string? title, string? description, IEnumerable<PromptArgumentInfo> arguments)
        => new(
            Name: TextSanitizer.Clean(name, TextSanitizer.NameLength),
            DisplayName: NameHumanizer.DisplayName(title, name),
            Summary: TextSanitizer.FirstSentence(description),
            FullDescription: TextSanitizer.CleanMultiline(description),
            // Fetching a prompt changes nothing, but "read-only" on every prompt in the tree would be
            // a word said a hundred times for no decision; unstated says nothing.
            Safety: OperationSafety.Unstated,
            Form: FormBuilder.Build(Schema(arguments)))
        {
            Kind = OperationKind.Prompt,
        };
}

using System.Text.Json;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// What running an operation does: call a tool, fetch a prompt's messages (Phase 12), or read a
/// resource (Phase 13). All of them share a form, a window and a result view, and differ in the
/// request and in a handful of words.
/// </summary>
public enum OperationKind
{
    Tool,

    /// <summary>
    /// A prompt: arguments in, messages out. Every argument is a string (the protocol has no schema
    /// for them), and fetching one changes nothing, so there is no safety to state.
    /// </summary>
    Prompt,

    /// <summary>A resource at a fixed address: no arguments, read as it stands.</summary>
    Resource,

    /// <summary>
    /// A resource template (RFC 6570): its variables are the form, and the address is made from
    /// what is typed into them. Built by <see cref="ResourceForm"/>.
    /// </summary>
    ResourceTemplate,
}

/// <summary>
/// One invocable operation, in terms the UI can use. Everything here has already been through
/// <see cref="TextSanitizer"/>: it arrived from a third party and is on its way to UI Automation
/// and a speech synthesiser (ROADMAP §4, pitfall 5).
/// </summary>
/// <remarks>
/// No MCP vocabulary, per ROADMAP §6.1 hedge 2 — this record has to read correctly for a non-MCP
/// capability source. Since Phase 2 the arguments are a <see cref="Schema.FormModel"/> rather than a
/// raw <see cref="JsonElement"/>: the schema engine has already taken the schema apart, and every
/// question the app asks about a tool's parameters is answered from that one model.
/// </remarks>
public sealed record ToolInfo(
    string Name,
    string DisplayName,
    string Summary,
    string FullDescription,
    OperationSafety Safety,
    FormModel Form,
    JsonElement? OutputSchema = null)
{
    /// <summary>A tool unless said otherwise. A prompt is built by <see cref="PromptForm"/>.</summary>
    public OperationKind Kind { get; init; } = OperationKind.Tool;

    public bool IsPrompt => Kind == OperationKind.Prompt;

    /// <summary>A resource or a resource template: read, never run.</summary>
    public bool IsResource => Kind is OperationKind.Resource or OperationKind.ResourceTemplate;

    /// <summary>
    /// A resource's address, or a template's RFC 6570 pattern. Empty for anything else. Not
    /// sanitised for speech: it is sent back to the server exactly as the server sent it.
    /// </summary>
    public string ResourceUri { get; init; } = "";

    /// <summary>What the resource says it is, where it said. Sanitised.</summary>
    public string MimeType { get; init; } = "";

    /// <summary>What it is called in a sentence: "tool", "prompt", "resource".</summary>
    public string Noun => Kind switch
    {
        OperationKind.Prompt => "prompt",
        OperationKind.Resource => "resource",
        OperationKind.ResourceTemplate => "resource template",
        _ => "tool",
    };

    /// <summary>The words of the run command: a prompt is fetched and a resource read, not run.</summary>
    public string RunVerb => Kind switch
    {
        OperationKind.Prompt => "Get prompt",
        OperationKind.Resource or OperationKind.ResourceTemplate => "Read resource",
        _ => "Run tool",
    };

    /// <summary>"Running", "Getting" or "Reading", for the status line while it happens.</summary>
    public string Doing => Kind switch
    {
        OperationKind.Prompt => "Getting",
        OperationKind.Resource or OperationKind.ResourceTemplate => "Reading",
        _ => "Running",
    };

    /// <summary>
    /// What one input is called: a tool's "parameter", and the protocol's "argument" for a prompt's
    /// and for a template's variables, which nobody outside RFC 6570 calls variables.
    /// </summary>
    public string ArgumentWord => Kind == OperationKind.Tool ? "parameter" : "argument";

    /// <summary>The schema exactly as the server sent it, for Copy Schema and for validation.</summary>
    public JsonElement InputSchema => Form.Schema;

    /// <summary>
    /// The schema for what a call answers with, where the source published one — 25 of the corpus's
    /// 213 tools do. Paired with the value by the result renderer, which takes the two together and
    /// nothing else (ROADMAP §6.1, hedge 5).
    /// </summary>
    public bool DescribesItsOutput => OutputSchema is { ValueKind: not JsonValueKind.Undefined };

    /// <summary>Top-level parameters. Nested fields are inside <see cref="Form"/>.</summary>
    public int ParameterCount => Form.Fields.Count;

    public int RequiredParameterCount => Form.RequiredCount;

    /// <summary>
    /// True when the tool can be invoked with no arguments at all — which is the whole of Phase 1's
    /// call surface, until form generation lands in Phase 3. Optional parameters do not stop a tool
    /// being runnable this way; a required one does.
    /// </summary>
    public bool RunsWithoutArguments => RequiredParameterCount == 0;

    /// <summary>
    /// What a screen reader reads when the user arrows onto this tool. Safety comes before the
    /// parameter count because it is the part that matters before pressing Enter.
    /// </summary>
    public string AccessibleName => Describe(server: null);

    /// <summary>
    /// <see cref="AccessibleName"/> with the server's name after the tool's, for a list that mixes
    /// servers — a search's results — where the tree's nesting is not there to say it.
    /// </summary>
    public string Describe(string? server)
    {
        var parts = new List<string> { DisplayName };

        if (!string.IsNullOrWhiteSpace(server)) parts.Add(server);
        if (Safety.ShortDescription is { } safety) parts.Add(safety);

        // A prompt's are "arguments" in the protocol and in every client that shows them.
        var word = ArgumentWord;

        // What a resource is matters more than how it is read; a template says that it is one,
        // because its form is an address being built rather than a request being made.
        if (Kind == OperationKind.Resource && MimeType.Length > 0) parts.Add(MimeType);
        if (Kind == OperationKind.ResourceTemplate) parts.Add("template");

        if (RequiredParameterCount > 0)
            parts.Add($"{RequiredParameterCount} required {word}{(RequiredParameterCount == 1 ? "" : "s")}");
        else if (ParameterCount > 0)
            parts.Add($"{ParameterCount} optional {word}{(ParameterCount == 1 ? "" : "s")}");

        return string.Join(", ", parts);
    }
}

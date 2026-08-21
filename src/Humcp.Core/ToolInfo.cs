using System.Text.Json;
using Humcp.Schema;

namespace Humcp.Core;

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
    FormModel Form)
{
    /// <summary>The schema exactly as the server sent it, for Copy Schema and for validation.</summary>
    public JsonElement InputSchema => Form.Schema;

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
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { DisplayName };

            if (Safety.ShortDescription is { } safety) parts.Add(safety);

            if (RequiredParameterCount > 0)
                parts.Add($"{RequiredParameterCount} required parameter{(RequiredParameterCount == 1 ? "" : "s")}");
            else if (ParameterCount > 0)
                parts.Add($"{ParameterCount} optional parameter{(ParameterCount == 1 ? "" : "s")}");

            return string.Join(", ", parts);
        }
    }
}

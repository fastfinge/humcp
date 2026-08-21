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
/// capability source. <see cref="InputSchema"/> is a bare <see cref="JsonElement"/> for the same
/// reason: it is what Phase 2's schema engine consumes.
/// </remarks>
public sealed record ToolInfo(
    string Name,
    string DisplayName,
    string Summary,
    string FullDescription,
    OperationSafety Safety,
    int ParameterCount,
    int RequiredParameterCount,
    JsonElement InputSchema)
{
    /// <summary>
    /// True when the tool can be invoked with no arguments at all — which is the whole of Phase 1's
    /// call surface, until form generation lands in Phase 3.
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

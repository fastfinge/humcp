using System.Text;
using Humcp.Core;

namespace Humcp.App;

/// <summary>
/// Turns a <see cref="ToolCallOutcome"/> into the text of the read-only result box.
/// </summary>
/// <remarks>
/// Phase 1 renders text and describes everything else as an object. Phase 4 replaces this with a
/// real renderer that takes a schema and a value (ROADMAP §6.1, hedge 5) and produces a grid or a
/// property tree — which is why this deliberately knows nothing about where the result came from.
/// </remarks>
internal static class ResultFormatter
{
    public static string Render(string toolDisplayName, ToolCallOutcome outcome, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var sb = new StringBuilder();
        var status = outcome.IsError ? "error" : "complete";

        sb.AppendLine($"{toolDisplayName} — {status} at {when.ToLocalTime():HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine();

        if (outcome.Blocks.Count == 0) sb.AppendLine("The tool returned no content.");

        for (var i = 0; i < outcome.Blocks.Count; i++)
        {
            if (i > 0) sb.AppendLine();

            switch (outcome.Blocks[i])
            {
                case TextResultBlock text:
                    sb.AppendLine(text.Text);
                    break;

                // Announced as an object rather than dropped: a tool that returns a chart must not
                // leave the user with nothing (ROADMAP §4, pitfall 6).
                case ObjectResultBlock other:
                    sb.AppendLine($"[{other.Description}]");
                    break;
            }
        }

        if (outcome.StructuredJson is { Length: > 0 } structured)
        {
            sb.AppendLine();
            sb.AppendLine("Structured content:");
            sb.AppendLine(structured);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// What prism says when a call finishes — a verb, and a route to the thing it produced. UI
    /// Automation has no way to announce "that finished" (ROADMAP §7).
    /// </summary>
    public static string Announcement(ToolCallOutcome outcome, string resultText)
    {
        var lines = resultText.Length == 0 ? 0 : resultText.Split('\n').Length;

        if (outcome.IsError)
        {
            var first = outcome.Blocks.OfType<TextResultBlock>().FirstOrDefault()?.Text ?? "";
            var detail = first.Split('\n', 2)[0];
            return detail.Length > 0
                ? $"Tool returned an error. {detail}"
                : "Tool returned an error.";
        }

        return $"Tool call complete. {lines} line{(lines == 1 ? "" : "s")}. Press F6 to review the result.";
    }
}

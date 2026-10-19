using ModelContextProtocol.Server;

namespace Humcp.TestServer;

/// <summary>
/// A tool that changes the tool list, so <c>notifications/tools/list_changed</c> has something to
/// say (ROADMAP §10, Phase 6). Pressing it adds a second tool; pressing it again takes that tool
/// away. The SDK sends the notification itself when the collection changes — unprompted to a client
/// on an older revision, and down a <c>subscriptions/listen</c> stream to one on 2026-07-28.
/// </summary>
internal static class ListChangeTools
{
    public const string ToggleName = "tools_toggle";
    public const string AppearedName = "tools_appeared";

    /// <summary>The server's own collection, set once at start-up by <see cref="Program"/>.</summary>
    public static McpServerPrimitiveCollection<McpServerTool>? Collection { get; set; }

    private static readonly McpServerTool Appeared = McpServerTool.Create(
        () => "This tool was not here when the server started.",
        new McpServerToolCreateOptions
        {
            Name = AppearedName,
            Title = "A tool that appeared",
            Description = "Published only after Add or remove a tool has been called.",
            ReadOnly = true,
        });

    /// <summary>
    /// Five steps over two and a half seconds, each with a message — enough for a client's rate
    /// limit to have something to drop (§4, pitfall 9).
    /// </summary>
    public static McpServerTool Progress() => McpServerTool.Create(
        async (IProgress<ModelContextProtocol.ProgressNotificationValue> progress, CancellationToken cancellationToken) =>
        {
            for (var step = 1; step <= 5; step++)
            {
                await Task.Delay(500, cancellationToken);
                progress.Report(new ModelContextProtocol.ProgressNotificationValue
                {
                    Progress = step,
                    Total = 5,
                    Message = $"Step {step} of 5",
                });
            }

            return "Finished all five steps.";
        },
        new McpServerToolCreateOptions
        {
            Name = "demo_progress",
            Title = "Report progress",
            Description = "Takes two and a half seconds and reports progress five times on the way.",
            ReadOnly = true,
        });

    public static McpServerTool Toggle() => McpServerTool.Create(
        () =>
        {
            if (Collection is null) return "The server has no tool collection to change.";

            if (Collection.TryGetPrimitive(AppearedName, out var existing))
            {
                Collection.Remove(existing);
                return "Removed A tool that appeared.";
            }

            Collection.Add(Appeared);
            return "Added A tool that appeared.";
        },
        new McpServerToolCreateOptions
        {
            Name = ToggleName,
            Title = "Add or remove a tool",
            Description = "Adds a tool to this server's list, or removes it if it is already there, " +
                          "so that the client is told the list has changed.",
            Idempotent = false,
        });
}

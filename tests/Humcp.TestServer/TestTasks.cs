using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humcp.TestServer;

/// <summary>
/// The fixture's tasks (ROADMAP §11, Phase 14): tools the server runs in the background when the
/// client offers to take a task back, one per thing a task can do to a client.
/// </summary>
/// <remarks>
/// Only these become tasks. The tasks extension as the SDK ships it makes every tool a task once a
/// client offers, and the fixture's other thirty-two tools are the subject of tests written for calls
/// that answer at once; <see cref="Mode"/> keeps them that way.
/// </remarks>
internal static class TestTasks
{
    public static IEnumerable<McpServerTool> All()
    {
        yield return SlowReport();
        yield return Wait();
        yield return Ask();
        yield return Fail();
    }

    /// <summary>How many tools this file publishes.</summary>
    public const int Count = 4;

    private const string Prefix = "task_";

    /// <summary>Which calls run in the background: these, and no others.</summary>
    public static McpTaskExecutionMode Mode(RequestContext<CallToolRequestParams> request)
        => request.Params?.Name?.StartsWith(Prefix, StringComparison.Ordinal) == true
            ? McpTaskExecutionMode.Optional
            : McpTaskExecutionMode.Synchronous;

    /// <summary>
    /// Nothing to fill in, five seconds of work, and progress on the way: the task the main window
    /// runs straight from the tree, and hands to the background list when it outlasts the three
    /// seconds the main window waits — by long enough that a test is never a race against it.
    /// </summary>
    private static McpServerTool SlowReport() => McpServerTool.Create(
        async (IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken) =>
        {
            for (var section = 1; section <= 3; section++)
            {
                await Task.Delay(1600, cancellationToken);
                progress.Report(new ProgressNotificationValue
                {
                    Progress = section,
                    Total = 3,
                    Message = $"Section {section} of 3 written",
                });
            }

            return "The report is ready. It has three sections: rainfall, wind and sunshine.";
        },
        new McpServerToolCreateOptions
        {
            Name = "task_slow_report",
            Title = "Write a slow report",
            Description = "Writes a report in the background, which takes five seconds.",
            ReadOnly = true,
        });

    /// <summary>As long as asked for, and stops when told to: the task that is still running when a test wants it running.</summary>
    private static McpServerTool Wait() => McpServerTool.Create(
        async ([Description("How long to work, in seconds.")] int seconds, CancellationToken cancellationToken) =>
        {
            var length = Math.Clamp(seconds, 0, 600);
            await Task.Delay(TimeSpan.FromSeconds(length), cancellationToken);
            return $"Worked for {length} seconds.";
        },
        new McpServerToolCreateOptions
        {
            Name = "task_wait",
            Title = "Work in the background",
            Description = "Works for as many seconds as it is told, and stops when it is asked to.",
            ReadOnly = true,
        });

    /// <summary>
    /// Asks a question in the middle of the work. Inside a task the SDK turns the question into an
    /// input request the client finds by asking after the task, and answers with <c>tasks/update</c>.
    /// </summary>
    private static McpServerTool Ask() => McpServerTool.Create(
        async (McpServer server, CancellationToken cancellationToken) =>
        {
            await Task.Delay(300, cancellationToken);

            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Mode = "form",
                Message = "What should the report be called?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["title"] = new ElicitRequestParams.StringSchema
                        {
                            Title = "Report title",
                            Description = "The name to put at the top of the report.",
                        },
                    },
                    Required = ["title"],
                },
            }, cancellationToken);

            if (!answer.IsAccepted) return $"No title was given ({answer.Action}), so the report is untitled.";

            var title = answer.Content?.TryGetValue("title", out var value) == true ? value.ToString() : "";
            return $"The report is called {title}.";
        },
        new McpServerToolCreateOptions
        {
            Name = "task_ask",
            Title = "Ask during a task",
            Description = "Asks what the report should be called, in the middle of writing it in the background.",
            ReadOnly = true,
        });

    /// <summary>A protocol error, not a tool's error: the task ends as failed rather than as a result.</summary>
    private static McpServerTool Fail() => McpServerTool.Create(
        async (CancellationToken cancellationToken) =>
        {
            await Task.Delay(300, cancellationToken);
            throw new McpProtocolException("The archive this task needed is missing.", McpErrorCode.InternalError);
        },
        new McpServerToolCreateOptions
        {
            Name = "task_fail",
            Title = "Fail in the background",
            Description = "Starts in the background and then fails with a protocol error.",
            ReadOnly = true,
        });
}

using System.Text.Json.Nodes;
using Humcp.Core;

namespace Humcp.Core.Tests;

/// <summary>
/// The tasks extension over a real pipe (ROADMAP §11, Phase 14): the fixture's <c>task_</c> tools
/// run in the background, and nothing else on the server does.
/// </summary>
public class TaskRoundTripTests(TestServerFixture fixture) : IClassFixture<TestServerFixture>
{
    private ServerConnection Connection => fixture.Connection;

    private ToolInfo Tool(string name)
        => Connection.Tools.SingleOrDefault(t => t.Name == name)
           ?? throw new InvalidOperationException(
               $"The fixture published no tool called {name}. It published: " +
               string.Join(", ", Connection.Tools.Select(t => t.Name)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_server_offers_tasks()
        => Assert.True(Connection.SupportsTasks, "The fixture advertises io.modelcontextprotocol/tasks on 2026-07-28.");

    [Fact]
    public async Task A_task_ends_with_the_result_a_call_would_have_had()
    {
        BackgroundTask? task = null;

        var outcome = await Connection.CallAsync(Tool("task_slow_report"), null, null, null, t => task = t, Ct);

        Assert.NotNull(task);
        Assert.False(outcome.IsError, outcome.CombinedText);
        Assert.Contains("three sections", outcome.CombinedText);
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Same(outcome, task.Outcome);
        Assert.Contains("Write a slow report on humcp test server, finished", task.Describe());
    }

    [Fact]
    public async Task A_task_reports_progress_like_a_call()
    {
        var reports = new List<ToolProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await Connection.CallAsync(Tool("task_slow_report"), null, null, progress, null, Ct);

        // Sent after the answer that created the task, on a request that has already ended.
        Assert.Contains(reports, r => r.Message.Contains("Section 1 of 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_other_tool_is_still_an_ordinary_call()
    {
        var made = false;

        var outcome = await Connection.CallAsync(Tool("demo_echo"), new JsonObject { ["text"] = "plain" },
            null, null, _ => made = true, Ct);

        Assert.False(made, "A synchronous tool on a server that offers tasks answers at once.");
        Assert.Contains("plain", outcome.CombinedText);
    }

    [Fact]
    public async Task Stopping_a_task_asks_the_server_and_the_call_ends_stopped()
    {
        BackgroundTask? task = null;

        var call = Connection.CallAsync(Tool("task_wait"), new JsonObject { ["seconds"] = 60 }, null, null,
            t =>
            {
                task = t;
                _ = t.StopAsync();
            }, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(20), Ct));

        Assert.NotNull(task);
        Assert.Equal(BackgroundTaskState.Stopped, task.State);
        Assert.Contains("stopped", task.Describe());
    }

    [Fact]
    public async Task Not_waiting_any_more_leaves_the_work_running()
    {
        BackgroundTask? task = null;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = Connection.CallAsync(Tool("task_wait"), new JsonObject { ["seconds"] = 1 }, null, null,
            t =>
            {
                task = t;
                waiting.Cancel();
            }, waiting.Token);

        // The window closed: the call stops waiting...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.NotNull(task);

        // ...and the task goes on to its answer.
        var outcome = await task.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Contains("Worked for 1 seconds", outcome.CombinedText);
    }

    [Fact]
    public async Task A_task_that_asks_is_answered_through_the_same_prompter()
    {
        var asked = new List<ElicitationRequest>();

        var outcome = await Connection.CallAsync(Tool("task_ask"), null,
            (request, _) =>
            {
                asked.Add(request);
                return Task.FromResult(ElicitationAnswer.Accepted(new JsonObject { ["title"] = "Rain" }));
            },
            null, null, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var question = Assert.Single(asked);
        Assert.Equal("What should the report be called?", question.Message);
        Assert.Contains("The report is called Rain.", outcome.CombinedText);
    }

    [Fact]
    public async Task A_task_whose_window_closed_asks_whoever_took_it_over()
    {
        BackgroundTask? task = null;
        var asked = 0;

        var outcome = await Connection.CallAsync(Tool("task_ask"), null, null, null,
            t =>
            {
                task = t;
                t.Prompter = (_, _) =>
                {
                    asked++;
                    return Task.FromResult(ElicitationAnswer.Declined);
                };
            }, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(1, asked);
        Assert.Contains("No title was given", outcome.CombinedText);
    }

    [Fact]
    public async Task A_task_that_fails_says_why()
    {
        BackgroundTask? task = null;

        var outcome = await Connection.CallAsync(Tool("task_fail"), null, null, null, t => task = t, Ct)
            .WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.True(outcome.IsError);
        Assert.NotNull(task);
        Assert.Contains("archive", outcome.CombinedText);
        Assert.Equal(BackgroundTaskState.Failed, task.State);
    }

    /// <summary>Reports straight away, on the reporting thread: <see cref="Progress{T}"/> posts, and a test has no loop to post to.</summary>
    private sealed class SynchronousProgress(Action<ToolProgress> report) : IProgress<ToolProgress>
    {
        private readonly Lock _gate = new();

        public void Report(ToolProgress value)
        {
            lock (_gate) report(value);
        }
    }
}

/// <summary>What a task says about itself, and how often humcp asks after it. No server.</summary>
public class BackgroundTaskTests
{
    private static ServerConnection Server()
        => new(new ServerDescriptor("docs", "Docs server", "none", []));

    private static ToolInfo Report()
        => PromptForm.Build("report", "Weekly report", null, []);

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(0L, 250)]
    [InlineData(-5L, 250)]
    [InlineData(400L, 400)]
    [InlineData(10_000_000L, 30_000)]
    public void The_servers_suggested_interval_is_kept_within_reason(long? suggested, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ServerConnection.PollInterval(suggested));

    [Fact]
    public void A_running_task_says_what_where_and_when()
    {
        var task = BackgroundTask.Detached("t1", Report(), Server(), new DateTimeOffset(2026, 10, 23, 14, 5, 9, TimeSpan.Zero));

        Assert.StartsWith("Weekly report on Docs server, running, started ", task.Describe());
        Assert.False(task.IsFinished);
        Assert.Equal("", task.Ending());
    }

    [Fact]
    public void Only_the_first_end_counts()
    {
        var task = BackgroundTask.Detached("t1", Report(), Server(), DateTimeOffset.Now);
        var result = new ToolCallOutcome(false, [new TextResultBlock("Done.")]);

        task.Finish(BackgroundTaskState.Completed, result);
        task.Finish(BackgroundTaskState.Lost, ToolCallOutcome.Failure("late"), "late");

        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Same(result, task.Outcome);
        Assert.Equal("Weekly report has finished. Control plus T shows its result.", task.Ending());
        Assert.Contains(", finished, started ", task.Describe());
    }

    [Fact]
    public void A_lost_task_says_why_it_did_not_finish()
    {
        var task = BackgroundTask.Detached("t1", Report(), Server(), DateTimeOffset.Now);

        task.Finish(BackgroundTaskState.Lost, ToolCallOutcome.Failure("gone"), "The connection to Docs server ended.");

        Assert.Equal("Weekly report did not finish. The connection to Docs server ended.", task.Ending());
        Assert.Contains("lost", task.Describe());
    }

    [Fact]
    public async Task A_stopped_task_ends_its_wait_as_a_stopped_call_does()
    {
        var task = BackgroundTask.Detached("t1", Report(), Server(), DateTimeOffset.Now);

        task.Finish(BackgroundTaskState.Stopped, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Completion);
        Assert.Equal("Weekly report was stopped.", task.Ending());
    }
}

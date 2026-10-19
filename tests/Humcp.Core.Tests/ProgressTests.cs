namespace Humcp.Core.Tests;

/// <summary>
/// Progress, and which of it is said (ROADMAP §4, pitfall 9). The throttle against a clock the test
/// moves; the reports against the real fixture over a real pipe.
/// </summary>
public class ProgressTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    }

    private static readonly ToolProgress Half = new(1, 2, "Halfway");

    [Fact]
    public void Nothing_is_said_in_the_first_interval()
    {
        var clock = new Clock();
        var throttle = new ProgressThrottle(() => clock.Now);

        // "Running" was just said, and a call that takes a second should go straight to its result.
        clock.Now += TimeSpan.FromSeconds(1.9);
        Assert.Null(throttle.Offer("Search", Half));

        clock.Now += TimeSpan.FromSeconds(0.2);
        Assert.Equal("Search, 50 percent. Halfway", throttle.Offer("Search", Half));
    }

    [Fact]
    public void At_most_one_report_every_interval_and_the_rest_are_dropped()
    {
        var clock = new Clock();
        var throttle = new ProgressThrottle(() => clock.Now);
        clock.Now += ProgressThrottle.Interval;

        Assert.NotNull(throttle.Offer("t", Half));

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(throttle.Offer("t", Half));

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.NotNull(throttle.Offer("t", Half));
    }

    [Fact]
    public void Nothing_is_said_after_the_call_has_finished()
    {
        var clock = new Clock();
        var throttle = new ProgressThrottle(() => clock.Now);

        throttle.Finish();
        clock.Now += TimeSpan.FromMinutes(1);

        // A report that arrives just after the answer would land on top of the answer, which is
        // phase5-notes 4.4 again.
        Assert.Null(throttle.Offer("t", Half));
    }

    [Theory]
    [InlineData(3, 4.0, "Reading", "Fetch, 75 percent. Reading")]
    [InlineData(3, null, "Reading", "Fetch: Reading")]
    [InlineData(3, null, "", "Fetch, step 3.")]
    [InlineData(9, 4.0, "", "Fetch, 100 percent.")]
    public void Says_a_percentage_only_when_there_is_a_total(double progress, double? total, string message, string expected)
        => Assert.Equal(expected, ProgressThrottle.Describe("Fetch", new ToolProgress(progress, total, message)));

    [Fact]
    public async Task Progress_from_a_real_server_arrives_as_neutral_reports()
    {
        await using var connection = new ServerConnection(TestServerFixture.TestServer());
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        var reports = new List<ToolProgress>();
        var progress = new Recorder(reports);

        var outcome = await connection.CallAsync(
            connection.Tools.Single(t => t.Name == "demo_progress"), null, null, progress,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);

        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.All(reports, r => Assert.Equal(5, r.Total));
            Assert.StartsWith("Step ", reports[0].Message);
        }
    }

    private sealed class Recorder(List<ToolProgress> into) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
            lock (into) into.Add(value);
        }
    }
}

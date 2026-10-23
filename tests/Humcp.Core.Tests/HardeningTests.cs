using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// Phase 7: Streamable HTTP, a server that dies mid-session, and a listing the SDK cannot read
/// whole — each against the real fixture, over a real pipe or a real socket.
/// </summary>
public class HardeningTests
{
    private static ServerDescriptor Fixture(params string[] arguments)
    {
        var located = TestServerFixture.TestServer();
        return located with { Arguments = [.. located.Arguments, .. arguments] };
    }

    // ------------------------------------------------------------------ HTTP

    [Fact]
    public async Task A_server_reached_by_address_connects_and_answers()
    {
        var port = FreePort();
        using var server = StartHttpFixture(port);

        await using var connection = new ServerConnection(new ServerDescriptor("http", "HTTP fixture", "", [])
        {
            Url = $"http://127.0.0.1:{port}/mcp",
        });

        await WaitForListening(server);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(connection.State == ServerConnectionState.Connected,
            $"Did not connect over HTTP: {connection.FailureReason}");
        Assert.Equal(32, connection.Tools.Count);

        var outcome = await connection.CallAsync(connection.Tools.Single(t => t.Name == "demo_echo"),
            new System.Text.Json.Nodes.JsonObject { ["text"] = "over the wire" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);
        Assert.Contains("over the wire", Assert.IsType<TextResultBlock>(outcome.Blocks[0]).Text);
    }

    [Theory]
    [InlineData("ftp://example.com/mcp")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("not an address")]
    public async Task An_address_that_is_not_http_is_refused_without_an_attempt(string url)
    {
        await using var connection = new ServerConnection(new ServerDescriptor("bad", "bad", "", []) { Url = url });

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.Failed, connection.State);
        Assert.Contains("https://", connection.FailureReason);
    }

    [Fact]
    public async Task An_address_nobody_answers_at_fails_with_a_reason()
    {
        await using var connection = new ServerConnection(new ServerDescriptor("gone", "gone", "", [])
        {
            Url = $"http://127.0.0.1:{FreePort()}/mcp",
        });

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.Failed, connection.State);
        Assert.False(string.IsNullOrWhiteSpace(connection.FailureReason));
    }

    // ------------------------------------------------------------------ a server that dies

    [Fact]
    public async Task A_server_that_dies_mid_session_is_reported_rather_than_left_connected()
    {
        await using var connection = new ServerConnection(Fixture("--exit-after", "1500"));
        var changed = new SemaphoreSlim(0);
        connection.Changed += (_, _) => changed.Release();

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ServerConnectionState.Connected, connection.State);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (connection.State == ServerConnectionState.Connected && DateTime.UtcNow < deadline)
            await changed.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.Failed, connection.State);
        Assert.StartsWith("It exited with code 3.", connection.FailureReason);

        // "Server said" quotes the server's own standard error, never humcp's diagnostics — found by
        // ear, when humcp's note about the listen stream failing was read out as the server's words.
        Assert.Contains("Server said: humcp test server starting", connection.FailureReason);
        Assert.DoesNotContain("subscription", connection.FailureReason);
        Assert.Empty(connection.Tools);

        // A call made now says so, rather than hanging on a pipe with nobody at the other end.
        var outcome = await connection.CallAsync(
            ToolTreeBuilderTests.Tool("demo_echo"), TestContext.Current.CancellationToken);
        Assert.True(outcome.IsError);
    }

    [Fact]
    public async Task A_server_humcp_stops_itself_is_not_reported_as_having_died()
    {
        await using var connection = new ServerConnection(TestServerFixture.TestServer());
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        await connection.StopAsync();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.NotConnected, connection.State);
        Assert.Null(connection.FailureReason);
    }

    // ------------------------------------------------------------------ a listing the SDK refuses

    [Fact]
    public async Task One_malformed_schema_costs_one_tool_its_form_and_nothing_else()
    {
        await using var connection = new ServerConnection(Fixture("--malformed"));
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(connection.State == ServerConnectionState.Connected,
            $"A malformed schema failed the whole server: {connection.FailureReason}");

        // Every ordinary tool, and the broken one too — as a JSON editor, which is what humcp does
        // with any schema it cannot draw (§5, Tier 3).
        Assert.Equal(33, connection.Tools.Count);

        var broken = connection.Tools.Single(t => t.Name == "broken_schema");
        Assert.Equal("A tool with a broken schema", broken.DisplayName);
        Assert.Contains(connection.Diagnostics, d => d.Contains("each tool was read on its own"));

        // The rest still work, read the long way round.
        var echo = connection.Tools.Single(t => t.Name == "demo_echo");
        Assert.Equal(FieldKind.Text, echo.Form.Fields.Single(f => f.Name == "text").Kind);
    }

    // ------------------------------------------------------------------ helpers

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class HttpFixture(Process process) : IDisposable
    {
        public Process Process { get; } = process;
        public TaskCompletionSource Listening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
            Process.Dispose();
        }
    }

    private static HttpFixture StartHttpFixture(int port)
    {
        var located = TestServerFixture.TestServer();
        var start = new ProcessStartInfo(located.Command)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in located.Arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--http");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(start) ?? throw new InvalidOperationException("The HTTP fixture did not start.");
        var fixture = new HttpFixture(process);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data?.Contains("listening on", StringComparison.Ordinal) == true) fixture.Listening.TrySetResult();
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        return fixture;
    }

    private static async Task WaitForListening(HttpFixture fixture)
    {
        var finished = await Task.WhenAny(fixture.Listening.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == fixture.Listening.Task, "The HTTP fixture never said it was listening.");
    }
}

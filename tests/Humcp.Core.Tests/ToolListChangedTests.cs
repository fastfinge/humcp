namespace Humcp.Core.Tests;

/// <summary>
/// <c>notifications/tools/list_changed</c>, over a real pipe from a real server that changes its
/// own list (ROADMAP §10, Phase 6). Each test has a connection of its own, because the server it
/// talks to is changed by it.
/// </summary>
public class ToolListChangedTests
{
    private static async Task<bool> Until(ServerConnection connection, Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var changed = new SemaphoreSlim(0);
        connection.Changed += (_, _) => changed.Release();

        while (!condition())
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            await changed.WaitAsync(left, TestContext.Current.CancellationToken);
        }

        return true;
    }

    [Fact]
    public async Task A_server_that_changes_its_tools_is_listed_again()
    {
        await using var connection = new ServerConnection(TestServerFixture.TestServer());
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(30, connection.Tools.Count);
        var toggle = connection.Tools.Single(t => t.Name == "tools_toggle");

        await connection.CallAsync(toggle, TestContext.Current.CancellationToken);

        Assert.True(await Until(connection, () => connection.Tools.Count == 31, TimeSpan.FromSeconds(15)),
            "The tool list never changed. Diagnostics: " + string.Join(" | ", connection.Diagnostics));
        Assert.Contains(connection.Tools, t => t.Name == "tools_appeared");

        await connection.CallAsync(toggle, TestContext.Current.CancellationToken);

        Assert.True(await Until(connection, () => connection.Tools.Count == 30, TimeSpan.FromSeconds(15)),
            "The tool that appeared never went away. Diagnostics: " + string.Join(" | ", connection.Diagnostics));
        Assert.DoesNotContain(connection.Tools, t => t.Name == "tools_appeared");
    }

    [Fact]
    public void The_change_is_said_in_one_sentence()
    {
        var before = new[] { ToolTreeBuilderTests.Tool("a_one"), ToolTreeBuilderTests.Tool("b_two") };
        var after = new[] { ToolTreeBuilderTests.Tool("a_one"), ToolTreeBuilderTests.Tool("c_three"), ToolTreeBuilderTests.Tool("d_four") };

        Assert.Equal("srv changed its tools: 2 added, C three and D four; 1 removed, B two. 3 tools now.",
            ServerAnnouncements.ToolsChanged("srv", before, after));

        Assert.Equal("", ServerAnnouncements.ToolsChanged("srv", before, before));
    }
}

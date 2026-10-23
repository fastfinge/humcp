namespace Humcp.Core.Tests;

/// <summary>
/// Several servers at once, and the file kept in step with the tree. Against real child processes:
/// the fixture, and an executable that does not exist.
/// </summary>
public sealed class ServerRegistryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "humcp-registry-tests", Guid.NewGuid().ToString("n"), "servers.json");

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }

    private static ServerDescriptor Broken(string id) => new(id, id, "humcp-there-is-no-such-executable", []);

    [Fact]
    public async Task One_server_failing_does_not_stop_the_others_connecting()
    {
        var configuration = ServerConfiguration.InMemory(TestServerFixture.TestServer(), Broken("broken"));
        await using var registry = new ServerRegistry(configuration);

        await registry.ConnectAllAsync(TestContext.Current.CancellationToken);

        var snapshots = registry.Snapshots();

        Assert.Equal(ServerConnectionState.Connected, snapshots[0].State);
        Assert.Equal(ServerConnectionState.Failed, snapshots[1].State);

        Assert.Equal("Connected to 1 of 2 servers, 36 tools, 4 prompts, 4 resources. Could not connect to broken.",
            ServerAnnouncements.Startup(snapshots));
    }

    [Fact]
    public async Task A_disabled_server_is_listed_and_not_started()
    {
        var configuration = ServerConfiguration.InMemory(Broken("off") with { Enabled = false });
        await using var registry = new ServerRegistry(configuration);

        await registry.ConnectAllAsync(TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(registry.Snapshots());

        Assert.Equal(ServerConnectionState.NotConnected, snapshot.State);
        Assert.Equal("off, disabled", ToolTreeBuilder.DescribeServer(snapshot));
    }

    [Fact]
    public async Task Adding_a_server_writes_the_file_and_starts_it()
    {
        await using var registry = new ServerRegistry(ServerConfiguration.Load(_path));
        var added = 0;
        registry.ServersChanged += (_, _) => added++;

        var connection = registry.Add(TestServerFixture.TestServer());

        Assert.NotNull(connection);
        Assert.Equal(1, added);
        Assert.Equal(ServerConnectionState.NotConnected, connection.State);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ServerConnectionState.Connected, connection.State);
        Assert.Equal("humcp-test-server", Assert.Single(ServerConfiguration.Load(_path).Servers).Id);
    }

    [Fact]
    public async Task Adding_a_server_under_a_name_in_use_gives_it_a_new_one()
    {
        await using var registry = new ServerRegistry(ServerConfiguration.Load(_path));

        registry.Add(Broken("same") with { Enabled = false });
        var second = registry.Add(Broken("same") with { Enabled = false });

        Assert.Equal("same-2", second!.Descriptor.Id);
        Assert.Equal(["same", "same-2"], ServerConfiguration.Load(_path).Servers.Select(s => s.Id));
    }

    [Fact]
    public async Task Editing_a_server_reconfigures_the_same_connection()
    {
        await using var registry = new ServerRegistry(ServerConfiguration.Load(_path));
        var connection = registry.Add(Broken("edit"))!;
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.Failed, connection.State);

        var updated = await registry.UpdateAsync(
            connection, TestServerFixture.TestServer() with { Id = "edit", DisplayName = "Edited" });

        Assert.True(updated);
        Assert.Equal(ServerConnectionState.NotConnected, connection.State);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        // The same object, now connected: a window that was holding it is holding a working server.
        Assert.Same(connection, registry.Find("edit"));
        Assert.Equal(ServerConnectionState.Connected, connection.State);
        Assert.Equal("Edited", Assert.Single(ServerConfiguration.Load(_path).Servers).DisplayName);
    }

    [Fact]
    public async Task Removing_a_server_takes_it_out_of_the_file()
    {
        await using var registry = new ServerRegistry(ServerConfiguration.Load(_path));
        var connection = registry.Add(Broken("gone") with { Enabled = false });

        Assert.True(registry.Remove(connection!));

        Assert.Empty(registry.Connections);
        Assert.Empty(ServerConfiguration.Load(_path).Servers);
    }

    [Fact]
    public async Task A_change_of_state_is_reported_with_its_reason_already_set()
    {
        await using var registry = new ServerRegistry(ServerConfiguration.InMemory(Broken("why")));
        var seen = new List<(ServerConnectionState, string?)>();

        registry.ServerChanged += (_, connection) => seen.Add((connection.State, connection.FailureReason));

        await registry.ConnectAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerConnectionState.Connecting, seen[0].Item1);
        Assert.Equal(ServerConnectionState.Failed, seen[^1].Item1);
        Assert.False(string.IsNullOrWhiteSpace(seen[^1].Item2));
    }

}

public class ServerAnnouncementsTests
{
    private static ServerSnapshot Server(string name, ServerConnectionState state, int tools = 0, bool enabled = true)
        => new(name, state, [.. Enumerable.Range(0, tools).Select(i => ToolTreeBuilderTests.Tool($"t{i}"))],
            state == ServerConnectionState.Connected ? "2026-07-28" : null,
            state == ServerConnectionState.Failed ? "it broke" : null)
        { Enabled = enabled };

    [Fact]
    public void One_server_is_announced_as_it_always_was()
        => Assert.Equal("Connected to a. 3 tools, protocol 2026-07-28.",
            ServerAnnouncements.Startup([Server("a", ServerConnectionState.Connected, 3)]));

    [Fact]
    public void Several_servers_are_one_sentence()
        => Assert.Equal("Connected to 2 servers, 5 tools.",
            ServerAnnouncements.Startup([
                Server("a", ServerConnectionState.Connected, 3),
                Server("b", ServerConnectionState.Connected, 2),
            ]));

    [Fact]
    public void Failures_are_named()
        => Assert.Equal("Connected to 1 of 3 servers, 1 tool. Could not connect to b and c.",
            ServerAnnouncements.Startup([
                Server("a", ServerConnectionState.Connected, 1),
                Server("b", ServerConnectionState.Failed),
                Server("c", ServerConnectionState.Failed),
            ]));

    [Fact]
    public void Disabled_servers_are_not_counted()
        => Assert.Equal("Connected to a. 1 tool, protocol 2026-07-28.",
            ServerAnnouncements.Startup([
                Server("a", ServerConnectionState.Connected, 1),
                Server("off", ServerConnectionState.NotConnected, enabled: false),
            ]));

    [Fact]
    public void Lists_read_as_english()
    {
        Assert.Equal("a", ServerAnnouncements.List(["a"]));
        Assert.Equal("a and b", ServerAnnouncements.List(["a", "b"]));
        Assert.Equal("a, b and c", ServerAnnouncements.List(["a", "b", "c"]));
    }
}

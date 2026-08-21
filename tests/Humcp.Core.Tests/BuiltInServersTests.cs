using Humcp.Core;

namespace Humcp.Core.Tests;

public class BuiltInServersTests
{
    [Fact]
    public void SplitArguments_splits_on_spaces()
    {
        Assert.Equal(["-y", "@modelcontextprotocol/server-everything"],
            BuiltInServers.SplitArguments("-y @modelcontextprotocol/server-everything"));
    }

    [Fact]
    public void SplitArguments_keeps_quoted_runs_together()
    {
        Assert.Equal(["run", "C:\\Program Files\\some server\\server.js", "--flag"],
            BuiltInServers.SplitArguments("run \"C:\\Program Files\\some server\\server.js\" --flag"));
    }

    [Fact]
    public void SplitArguments_of_nothing_is_empty()
    {
        Assert.Empty(BuiltInServers.SplitArguments(null));
        Assert.Empty(BuiltInServers.SplitArguments("   "));
    }

    [Fact]
    public void FromEnvironment_is_null_when_the_override_is_unset()
    {
        Assert.Null(BuiltInServers.FromEnvironment(_ => null));
    }

    [Fact]
    public void FromEnvironment_builds_a_descriptor_from_the_override()
    {
        var descriptor = BuiltInServers.FromEnvironment(name => name switch
        {
            BuiltInServers.CommandVariable => "npx",
            BuiltInServers.ArgumentsVariable => "-y @modelcontextprotocol/server-everything",
            _ => null,
        });

        Assert.NotNull(descriptor);
        Assert.Equal("npx", descriptor.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-everything"], descriptor.Arguments);
        Assert.Equal("npx", descriptor.DisplayName);
    }

    [Fact]
    public void FromEnvironment_honours_an_explicit_display_name()
    {
        var descriptor = BuiltInServers.FromEnvironment(name => name switch
        {
            BuiltInServers.CommandVariable => "npx",
            BuiltInServers.NameVariable => "everything",
            _ => null,
        });

        Assert.NotNull(descriptor);
        Assert.Equal("everything", descriptor.DisplayName);
    }

    [Fact]
    public void TestServer_resolves_to_something_that_exists()
    {
        var descriptor = BuiltInServers.TestServer(AppContext.BaseDirectory);

        Assert.Equal("humcp-test-server", descriptor.Id);
        Assert.True(descriptor.Command == "dotnet" || File.Exists(descriptor.Command),
            $"The fixture server was not found. Resolved command line: {descriptor.CommandLine}");
    }
}

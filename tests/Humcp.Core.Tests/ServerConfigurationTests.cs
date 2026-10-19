using System.Text.Json.Nodes;

namespace Humcp.Core.Tests;

/// <summary>
/// <c>servers.json</c>, and the text people paste into humcp from every other client's
/// documentation (ROADMAP §8, §10 Phase 6).
/// </summary>
public sealed class ServerConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "humcp-config-tests", Guid.NewGuid().ToString("n"));

    private string File(string text)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "servers.json");
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the run is not a failure */ }
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public void A_missing_file_is_a_first_run_rather_than_an_error()
    {
        var configuration = ServerConfiguration.Load(Path.Combine(_directory, "nothing-here.json"));

        Assert.Empty(configuration.Servers);
        Assert.Null(configuration.LastError);
    }

    [Fact]
    public void Reads_the_shape_every_other_client_writes()
    {
        var configuration = ServerConfiguration.Load(File("""
            {
              "mcpServers": {
                "everything": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-everything"],
                  "env": { "GITHUB_TOKEN": "ghp_secret", "PORT": 8080 }
                }
              }
            }
            """));

        var server = Assert.Single(configuration.Servers);

        Assert.Equal("everything", server.Id);
        Assert.Equal("everything", server.DisplayName);
        Assert.Equal("npx", server.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-everything"], server.Arguments);
        Assert.Equal("ghp_secret", server.Environment["GITHUB_TOKEN"]);

        // A number where a string belongs is what people type; the process gets the text of it.
        Assert.Equal("8080", server.Environment["PORT"]);
        Assert.True(server.Enabled);
    }

    [Fact]
    public void Reads_humcp_s_own_members_and_the_comments_vs_code_allows()
    {
        var configuration = ServerConfiguration.Load(File("""
            {
              // VS Code's mcp.json allows comments and trailing commas, and spells the block "servers".
              "servers": {
                "gh": { "command": "gh-mcp", "title": "GitHub", "disabled": true, "cwd": "C:\\work", },
              },
            }
            """));

        var server = Assert.Single(configuration.Servers);

        Assert.Equal("GitHub", server.DisplayName);
        Assert.False(server.Enabled);
        Assert.Equal("C:\\work", server.WorkingDirectory);
    }

    [Fact]
    public void A_web_address_is_kept_even_though_it_cannot_be_connected_to_yet()
    {
        var server = Assert.Single(ServerConfiguration.Load(File("""
            { "mcpServers": { "remote": { "type": "http", "url": "https://example.com/mcp" } } }
            """)).Servers);

        Assert.Equal("https://example.com/mcp", server.Url);
        Assert.Equal("https://example.com/mcp", server.CommandLine);
    }

    [Fact]
    public void An_entry_that_cannot_be_read_is_reported_and_the_rest_still_load()
    {
        var configuration = ServerConfiguration.Load(File("""
            { "mcpServers": { "good": { "command": "a" }, "bad": { "args": ["x"] }, "worse": 7 } }
            """));

        Assert.Equal("good", Assert.Single(configuration.Servers).Id);
        Assert.Equal(2, configuration.Problems.Count);
        Assert.Contains(configuration.Problems, p => p.StartsWith("bad has neither a command", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public void A_file_that_is_not_json_is_never_saved_over()
    {
        const string broken = "{ \"mcpServers\": { \"a\": { \"command\": ";
        var path = File(broken);

        var configuration = ServerConfiguration.Load(path);

        Assert.NotNull(configuration.LastError);
        Assert.Contains("has not changed it", configuration.LastError);

        configuration.Put(new ServerDescriptor("new", "new", "cmd", []));

        Assert.False(configuration.Save());
        Assert.Equal(broken, System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void A_save_changes_what_humcp_manages_and_nothing_else()
    {
        var path = File("""
            {
              "globalShortcut": "Ctrl+Space",
              "mcpServers": {
                "first": { "command": "a", "autoApprove": ["read"] },
                "second": { "command": "b" }
              }
            }
            """);

        var configuration = ServerConfiguration.Load(path);
        var first = configuration.Find("first")!;

        configuration.Put(first with { Command = "a2", Arguments = ["--flag"] });
        Assert.True(configuration.Save());

        var saved = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();

        // Another client's settings, at the top and inside an entry, survive humcp saving the file.
        Assert.Equal("Ctrl+Space", (string?)saved["globalShortcut"]);
        Assert.Equal("read", (string?)saved["mcpServers"]!["first"]!["autoApprove"]![0]);

        Assert.Equal("a2", (string?)saved["mcpServers"]!["first"]!["command"]);
        Assert.Equal("--flag", (string?)saved["mcpServers"]!["first"]!["args"]![0]);

        // And nothing humcp has no opinion about is written out at its default.
        Assert.Null(saved["mcpServers"]!["second"]!["disabled"]);
        Assert.Null(saved["mcpServers"]!["second"]!["title"]);
        Assert.Null(saved["mcpServers"]!["second"]!["env"]);
    }

    [Fact]
    public void Renaming_a_server_moves_its_entry_and_keeps_its_other_members()
    {
        var path = File("""{ "mcpServers": { "old": { "command": "a", "note": "mine" } } }""");
        var configuration = ServerConfiguration.Load(path);

        configuration.Put(configuration.Find("old")! with { Id = "new", DisplayName = "New one" }, previousId: "old");
        configuration.Save();

        var reloaded = ServerConfiguration.Load(path);
        var server = Assert.Single(reloaded.Servers);

        Assert.Equal("new", server.Id);
        Assert.Equal("New one", server.DisplayName);
        Assert.Equal("mine", (string?)JsonNode.Parse(System.IO.File.ReadAllText(path))!["mcpServers"]!["new"]!["note"]);
    }

    [Fact]
    public void Round_trips_everything_it_reads()
    {
        var path = Path.Combine(_directory, "servers.json");
        var configuration = ServerConfiguration.Load(path);

        var server = new ServerDescriptor("x", "Display name", "C:\\Program Files\\x\\x.exe", ["a b", "c"], "C:\\w")
        {
            Environment = new Dictionary<string, string> { ["K"] = "v" },
            Enabled = false,
        };

        configuration.Put(server);
        Assert.True(configuration.Save());

        var read = Assert.Single(ServerConfiguration.Load(path).Servers);

        Assert.Equal(server.Id, read.Id);
        Assert.Equal(server.DisplayName, read.DisplayName);
        Assert.Equal(server.Command, read.Command);
        Assert.Equal(server.Arguments, read.Arguments);
        Assert.Equal(server.WorkingDirectory, read.WorkingDirectory);
        Assert.Equal(server.Environment, read.Environment);
        Assert.False(read.Enabled);
    }

    [Fact]
    public void A_new_key_never_lands_on_an_entry_that_could_not_be_read()
    {
        var configuration = ServerConfiguration.Load(File("""{ "mcpServers": { "github": 7 } }"""));

        Assert.Empty(configuration.Servers);
        Assert.True(configuration.Taken("github"));
        Assert.Equal("github-2", configuration.UniqueId("github"));
    }

    // ------------------------------------------------------------------ pasting

    [Theory]
    [InlineData("""{ "mcpServers": { "everything": { "command": "npx", "args": ["-y", "x"] } } }""")]
    [InlineData("""{ "servers": { "everything": { "command": "npx", "args": ["-y", "x"] } } }""")]
    [InlineData("""{ "everything": { "command": "npx", "args": ["-y", "x"] } }""")]
    [InlineData(""" "everything": { "command": "npx", "args": ["-y", "x"] }, """)]
    public void Accepts_whatever_another_client_s_documentation_says_to_copy(string pasted)
    {
        var import = ServerConfiguration.Parse(pasted);

        Assert.Empty(import.Problems);
        var server = Assert.Single(import.Servers);
        Assert.Equal("everything", server.Id);
        Assert.Equal(["-y", "x"], server.Arguments);
    }

    [Fact]
    public void A_bare_entry_is_given_a_name()
    {
        var server = Assert.Single(ServerConfiguration.Parse("""{ "command": "uvx", "args": ["mcp-server-time"] }""").Servers);

        Assert.Equal("server", server.Id);
        Assert.Equal("uvx", server.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Text_that_names_no_server_says_so(string pasted)
    {
        var import = ServerConfiguration.Parse(pasted);

        Assert.Empty(import.Servers);
        Assert.NotEmpty(Assert.Single(import.Problems));
    }

    [Fact]
    public void Copy_writes_a_block_that_parse_reads_back()
    {
        var server = new ServerDescriptor("s", "s", "cmd", ["--x"]) { Environment = new Dictionary<string, string> { ["A"] = "1" } };

        var read = Assert.Single(ServerConfiguration.Parse(ServerConfiguration.ToJson([server])).Servers);

        Assert.Equal(server.Command, read.Command);
        Assert.Equal(server.Arguments, read.Arguments);
        Assert.Equal(server.Environment, read.Environment);
    }

    // ------------------------------------------------------------------ arguments as one line

    [Fact]
    public void SplitArguments_splits_on_spaces()
        => Assert.Equal(["-y", "@modelcontextprotocol/server-everything"],
            ServerDescriptor.SplitArguments("-y @modelcontextprotocol/server-everything"));

    [Fact]
    public void SplitArguments_honours_quotes_and_keeps_an_empty_quoted_argument()
        => Assert.Equal(["run", "C:\\Program Files\\some server\\server.js", "", "--flag"],
            ServerDescriptor.SplitArguments("run \"C:\\Program Files\\some server\\server.js\" \"\" --flag"));

    [Fact]
    public void SplitArguments_of_nothing_is_nothing()
    {
        Assert.Empty(ServerDescriptor.SplitArguments(null));
        Assert.Empty(ServerDescriptor.SplitArguments("   "));
    }

    [Fact]
    public void JoinArguments_is_the_inverse_of_SplitArguments()
    {
        string[] arguments = ["run", "C:\\Program Files\\x.js", "", "--flag"];

        Assert.Equal(arguments, ServerDescriptor.SplitArguments(ServerDescriptor.JoinArguments(arguments)));
    }
}

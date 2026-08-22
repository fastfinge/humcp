using Humcp.Core;

namespace Humcp.Core.Tests;

/// <summary>
/// The local substitute for an autocomplete the protocol does not offer (ROADMAP §4, pitfall 3),
/// and the rules about what it refuses to write to disk.
/// </summary>
public class MruHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "humcp-tests", Guid.NewGuid().ToString("n"), "history.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void An_unused_parameter_has_no_history()
    {
        Assert.Empty(MruHistory.Load(_path).For("github", "create_issue", "owner"));
    }

    [Fact]
    public void What_was_typed_comes_back_newest_first()
    {
        var history = MruHistory.Load(_path);

        history.Record("github", "create_issue", "owner", "octocat");
        history.Record("github", "create_issue", "owner", "ada");

        Assert.Equal(["ada", "octocat"], history.For("github", "create_issue", "owner"));
    }

    [Fact]
    public void The_same_value_twice_is_one_entry_moved_to_the_front()
    {
        var history = MruHistory.Load(_path);

        history.Record("github", "create_issue", "owner", "ada");
        history.Record("github", "create_issue", "owner", "octocat");
        history.Record("github", "create_issue", "owner", "ada");

        Assert.Equal(["ada", "octocat"], history.For("github", "create_issue", "owner"));
    }

    [Fact]
    public void History_is_kept_per_server_per_tool_and_per_parameter()
    {
        var history = MruHistory.Load(_path);

        history.Record("github", "create_issue", "owner", "ada");

        Assert.Empty(history.For("gitlab", "create_issue", "owner"));
        Assert.Empty(history.For("github", "list_issues", "owner"));
        Assert.Empty(history.For("github", "create_issue", "repo"));
    }

    [Fact]
    public void Only_the_last_ten_values_are_kept()
    {
        var history = MruHistory.Load(_path);

        for (var i = 0; i < 25; i++) history.Record("s", "t", "f", $"value {i}");

        var values = history.For("s", "t", "f");

        Assert.Equal(MruHistory.PerFieldLimit, values.Count);
        Assert.Equal("value 24", values[0]);
    }

    [Theory]
    [InlineData("apiToken")]
    [InlineData("api_key")]
    [InlineData("password")]
    [InlineData("auth.token")]
    [InlineData("headers.Authorization")]
    [InlineData("sessionSecret")]
    public void A_parameter_that_reads_like_a_credential_is_never_written_down(string path)
    {
        var history = MruHistory.Load(_path);

        Assert.False(history.Record("s", "t", path, "hunter2"));
        Assert.Empty(history.For("s", "t", path));
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("sortOrder")]
    [InlineData("monkey")]
    [InlineData("repo")]
    public void An_ordinary_parameter_that_merely_contains_those_letters_is_kept(string path)
    {
        // Matched on whole words after the name is broken up, so "keyword" is not "key".
        Assert.True(MruHistory.Load(_path).Record("s", "t", path, "value"));
    }

    [Fact]
    public void A_value_too_long_to_be_a_name_is_not_kept()
    {
        var history = MruHistory.Load(_path);

        Assert.False(history.Record("s", "t", "body", new string('x', MruHistory.MaxValueLength + 1)));
        Assert.False(history.Record("s", "t", "body", "two\nlines"));
        Assert.False(history.Record("s", "t", "body", "   "));
    }

    [Fact]
    public void Saved_history_is_there_on_the_next_run()
    {
        var history = MruHistory.Load(_path);
        history.Record("github", "create_issue", "owner", "octocat");
        history.Record("github", "create_issue", "owner", "ada");
        history.Save();

        Assert.Equal(["ada", "octocat"], MruHistory.Load(_path).For("github", "create_issue", "owner"));
    }

    [Fact]
    public void Forgetting_empties_the_file_as_well_as_the_memory()
    {
        var history = MruHistory.Load(_path);
        history.Record("github", "create_issue", "owner", "ada");
        history.Save();

        history.Forget();

        Assert.Empty(history.For("github", "create_issue", "owner"));
        Assert.Empty(MruHistory.Load(_path).For("github", "create_issue", "owner"));
    }

    [Fact]
    public void A_corrupt_file_is_an_empty_history_rather_than_a_crash()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json");

        var history = MruHistory.Load(_path);

        Assert.Empty(history.For("s", "t", "f"));
        Assert.NotNull(history.LastError);

        // And it recovers: the next save overwrites the mess rather than refusing forever.
        history.Record("s", "t", "f", "value");
        history.Save();

        Assert.Equal(["value"], MruHistory.Load(_path).For("s", "t", "f"));
    }

    [Fact]
    public void The_store_that_keeps_nothing_keeps_nothing()
    {
        var history = MruHistory.None;

        Assert.True(history.Record("s", "t", "f", "value"));
        history.Save();

        Assert.Equal(["value"], history.For("s", "t", "f"));
        Assert.False(File.Exists(_path));
    }
}

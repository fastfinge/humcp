using Humcp.Schema;

namespace Humcp.Schema.Tests;

/// <summary>
/// ROADMAP §7.1: a control's accessible name is humanised, because a screen reader reads
/// <c>list_pull_requests</c> as one long stumble.
/// </summary>
public class NameHumanizerTests
{
    [Theory]
    [InlineData("list_pull_requests", "List pull requests")]
    [InlineData("getTinyImage", "Get tiny image")]
    [InlineData("print-env", "Print env")]
    [InlineData("filesystem.read_file", "Filesystem read file")]
    [InlineData("ping", "Ping")]
    [InlineData("read_URL", "Read URL")]
    [InlineData("URLPath", "URL path")]
    [InlineData("issue2Comment", "Issue 2 comment")]
    public void Humanize_turns_identifiers_into_words(string identifier, string expected)
    {
        Assert.Equal(expected, NameHumanizer.Humanize(identifier));
    }

    [Fact]
    public void Humanize_of_nothing_is_empty()
    {
        Assert.Equal("", NameHumanizer.Humanize(null));
        Assert.Equal("", NameHumanizer.Humanize("   "));
    }

    [Fact]
    public void DisplayName_prefers_the_title_the_server_supplied()
    {
        Assert.Equal("Create issue", NameHumanizer.DisplayName("Create issue", "create_issue"));
    }

    [Fact]
    public void DisplayName_falls_back_to_the_humanised_name()
    {
        Assert.Equal("Create issue", NameHumanizer.DisplayName(null, "create_issue"));
        Assert.Equal("Create issue", NameHumanizer.DisplayName("  ", "create_issue"));
    }

    [Fact]
    public void DisplayName_sanitises_a_hostile_title()
    {
        var title = "Create" + char.ConvertFromUtf32(0x0007) + "issue";

        Assert.Equal("Create issue", NameHumanizer.DisplayName(title, "create_issue"));
    }
}

namespace Humcp.Core.Tests;

/// <summary>
/// Which links humcp hands to the shell. A <c>resource_link</c> is a string a server chose, and
/// opening a string with the shell runs whichever program claims its scheme.
/// </summary>
public class LinkPolicyTests
{
    [Theory]
    [InlineData("https://example.com/report.html")]
    [InlineData("http://localhost:8080/x?y=1")]
    public void A_web_address_goes_to_the_browser(string uri) =>
        Assert.Equal(LinkAction.Browse, LinkPolicy.Action(uri));

    [Theory]
    [InlineData("file:///C:/Windows/System32/notepad.exe")]
    [InlineData("file:///home/demo/notes.txt")]
    public void A_file_is_shown_in_a_folder_and_never_launched(string uri) =>
        Assert.Equal(LinkAction.Reveal, LinkPolicy.Action(uri));

    [Theory]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vscode://file/etc/passwd")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("not a uri at all")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_copyable_and_inert(string? uri)
    {
        Assert.Equal(LinkAction.None, LinkPolicy.Action(uri));

        // §7.18: an edge explains itself rather than being a button that does nothing.
        Assert.Contains("copied but not opened", LinkPolicy.Describe(uri));
        Assert.Equal("", LinkPolicy.ActionName(uri));
    }

    [Fact]
    public void An_address_carrying_credentials_is_one_to_look_at_rather_than_follow()
    {
        Assert.Equal(LinkAction.None, LinkPolicy.Action("https://user:secret@example.com/"));
    }
}

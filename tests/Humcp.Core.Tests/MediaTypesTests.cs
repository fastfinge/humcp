namespace Humcp.Core.Tests;

/// <summary>
/// The rule that decides what Windows is asked to run (ROADMAP §10, Phase 4). Bytes and a media
/// type both arrive from a third party, and "save this and open it" is the one action in the
/// application that ends in the shell choosing a program.
/// </summary>
public class MediaTypesTests
{
    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("audio/wav", ".wav")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("text/csv", ".csv")]
    public void A_type_on_the_list_gets_the_extension_it_really_is(string mime, string extension)
    {
        Assert.Equal(extension, MediaTypes.ExtensionFor(mime));
        Assert.True(MediaTypes.CanOpen(mime));
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/x-msdownload")]
    [InlineData("text/html")]
    [InlineData("")]
    [InlineData(null)]
    public void A_type_that_is_not_on_the_list_is_saved_and_never_opened(string? mime)
    {
        // Saved, because refusing would lose the user their data to protect them from a risk that
        // saving does not carry. Not opened, because "open" on an unrecognised type is "run
        // whatever this turns out to be".
        Assert.Equal(".bin", MediaTypes.ExtensionFor(mime));
        Assert.False(MediaTypes.CanOpen(mime));
    }

    [Fact]
    public void An_image_that_can_carry_script_is_saved_but_not_opened()
    {
        // SVG is an image everywhere else and a document here, and the program Windows opens it
        // with is a browser.
        Assert.Equal(".svg", MediaTypes.ExtensionFor("image/svg+xml"));
        Assert.False(MediaTypes.CanOpen("image/svg+xml"));
    }

    [Fact]
    public void Parameters_and_capitals_do_not_get_a_type_past_the_list()
    {
        Assert.Equal(".png", MediaTypes.ExtensionFor("IMAGE/PNG; charset=binary"));
        Assert.True(MediaTypes.CanOpen(" image/png "));
    }

    [Fact]
    public void A_part_says_what_it_is_because_nothing_else_will()
    {
        // §4, pitfall 6: no alt text exists anywhere in the protocol, so kind, type and size are
        // the whole of what can honestly be said.
        Assert.Equal("Image, PNG, 45.2 KB", MediaTypes.Describe(MediaKind.Image, "image/png", 46_300));
        Assert.Equal("Audio, WAV, 512 bytes", MediaTypes.Describe(MediaKind.Audio, "audio/wav", 512));
        Assert.Equal("File, 2 MB", MediaTypes.Describe(MediaKind.File, null, 2_097_152));
    }

    [Fact]
    public void The_file_name_offered_is_ours_and_not_the_servers()
    {
        Assert.Equal("image.png", MediaTypes.SuggestedFileName(MediaKind.Image, "image/png"));
        Assert.Equal("resource.bin", MediaTypes.SuggestedFileName(MediaKind.File, "application/x-anything"));
    }
}

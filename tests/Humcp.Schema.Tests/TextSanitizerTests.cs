using Humcp.Schema;

namespace Humcp.Schema.Tests;

/// <summary>
/// Every string a server sends is untrusted and is heading for UI Automation or a speech
/// synthesiser (ROADMAP §4, pitfall 5). These are the rules that hold at that boundary.
/// </summary>
public class TextSanitizerTests
{
    [Fact]
    public void Clean_removes_control_characters()
    {
        var input = "Bell" + char.ConvertFromUtf32(0x0007) + "and escape" + char.ConvertFromUtf32(0x001B) + "here";

        var cleaned = TextSanitizer.Clean(input);

        Assert.DoesNotContain(cleaned, c => char.IsControl(c));
        Assert.Equal("Bell and escape here", cleaned);
    }

    [Fact]
    public void Clean_removes_bidi_overrides_and_zero_width_characters()
    {
        const char rightToLeftOverride = (char)0x202E;
        const char popDirectionalFormatting = (char)0x202C;
        const char zeroWidthSpace = (char)0x200B;

        var cleaned = TextSanitizer.Clean(
            $"Safe {rightToLeftOverride}text{popDirectionalFormatting} and{zeroWidthSpace}more");

        // Formatting characters are removed outright rather than turned into spaces: they were
        // invisible to a sighted reader, so inventing a gap would change what the text says.
        // Asserted by code point rather than by substring - a linguistic Contains treats these
        // characters as ignorable and "finds" them in any string at all.
        Assert.Equal("Safe text andmore", cleaned);
        Assert.DoesNotContain(cleaned,
            c => c == rightToLeftOverride || c == popDirectionalFormatting || c == zeroWidthSpace);
    }

    [Fact]
    public void Clean_collapses_whitespace_and_newlines_into_one_line()
    {
        var cleaned = TextSanitizer.Clean("  one\r\n\r\n  two\t\tthree   ");

        Assert.Equal("one two three", cleaned);
    }

    [Fact]
    public void Clean_caps_length_including_the_ellipsis()
    {
        var cleaned = TextSanitizer.Clean(new string('x', 10_000), maxLength: 50);

        Assert.Equal(50, cleaned.Length);
        Assert.EndsWith("…", cleaned);
    }

    [Fact]
    public void Clean_prefers_a_word_boundary_when_truncating()
    {
        var cleaned = TextSanitizer.Clean("the quick brown fox jumps over the lazy dog", maxLength: 20);

        Assert.True(cleaned.Length <= 20);
        Assert.EndsWith("…", cleaned);
        Assert.DoesNotContain("jum…", cleaned);
    }

    [Fact]
    public void Clean_of_null_or_empty_is_empty()
    {
        Assert.Equal("", TextSanitizer.Clean(null));
        Assert.Equal("", TextSanitizer.Clean(""));
        Assert.Equal("", TextSanitizer.Clean("   \r\n  "));
    }

    [Fact]
    public void CleanMultiline_keeps_line_structure()
    {
        var cleaned = TextSanitizer.CleanMultiline("first\r\nsecond\r\nthird");

        Assert.Equal(3, cleaned.Split(Environment.NewLine).Length);
        Assert.Contains("second", cleaned);
    }

    [Fact]
    public void CleanMultiline_keeps_indentation()
    {
        // Results arrive as JSON and as aligned columns, and the read-only result box exists to be
        // read line by line. Collapsing the indentation would throw away the only structure such a
        // result has — which is exactly what Clean does, and why the two are separate.
        var cleaned = TextSanitizer.CleanMultiline("{\r\n    \"name\": \"value\"\r\n}");

        Assert.Contains("    \"name\": \"value\"", cleaned);
    }

    [Fact]
    public void CleanMultiline_trims_trailing_whitespace_per_line()
    {
        var cleaned = TextSanitizer.CleanMultiline("one   \r\ntwo\t\r\nthree");

        Assert.Equal(string.Join(Environment.NewLine, "one", "two", "three"), cleaned);
    }

    [Fact]
    public void CleanMultiline_still_removes_control_characters()
    {
        var cleaned = TextSanitizer.CleanMultiline("line one" + char.ConvertFromUtf32(0x0007) + "\r\nline two");

        Assert.DoesNotContain(cleaned, c => char.IsControl(c) && c is not '\r' and not '\n');
    }

    [Theory]
    [InlineData("One sentence. Another one.", "One sentence.")]
    [InlineData("No terminator at all", "No terminator at all")]
    [InlineData("Ends here!", "Ends here!")]
    [InlineData("Version 1.2 is fine. Second.", "Version 1.2 is fine.")]
    [InlineData("Use e.g. this one. Second.", "Use e.g. this one.")]
    public void FirstSentence_stops_at_the_first_real_sentence_end(string input, string expected)
    {
        Assert.Equal(expected, TextSanitizer.FirstSentence(input));
    }

    [Fact]
    public void FirstSentence_stops_at_a_line_break()
    {
        // Real descriptions open with a heading line and then paragraphs of instructions to a
        // language model. HelpText gets the heading, not the essay.
        var summary = TextSanitizer.FirstSentence("Create an issue\r\n\r\nIMPORTANT: always call list_repos first.");

        Assert.Equal("Create an issue", summary);
    }

    [Fact]
    public void FirstSentence_of_a_ten_thousand_character_description_is_short()
    {
        var hostile = "IMPORTANT: always call info_server first. " + new string('x', 10_000);

        var summary = TextSanitizer.FirstSentence(hostile);

        Assert.Equal("IMPORTANT: always call info_server first.", summary);
        Assert.True(summary.Length <= TextSanitizer.HelpLength);
    }

    [Fact]
    public void CleanForSpeech_caps_a_single_utterance()
    {
        var spoken = TextSanitizer.CleanForSpeech(new string('x', 50_000));

        Assert.True(spoken.Length <= TextSanitizer.SpeechLength);
    }
}

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humcp.TestServer;

/// <summary>
/// The fixture's tool set. Names share three prefixes on purpose - <c>info_</c>, <c>text_</c> and
/// <c>demo_</c> - so the client's prefix grouping (ROADMAP §8) has something to group and the tree
/// reaches the four levels Phase 0 proved WPF can carry.
/// </summary>
internal static class TestTools
{
    // Built from code points rather than pasted in. A raw bidi override sitting in a source file
    // is the same trick this fixture exists to defend against, and every tool between here and a
    // reviewer's screen renders one differently.
    private static readonly string Bell = char.ConvertFromUtf32(0x0007);
    private static readonly string VerticalTab = char.ConvertFromUtf32(0x000B);
    private static readonly string Escape = char.ConvertFromUtf32(0x001B);
    private static readonly string ZeroWidthSpace = char.ConvertFromUtf32(0x200B);
    private static readonly string RightToLeftOverride = char.ConvertFromUtf32(0x202E);
    private static readonly string PopDirectionalFormatting = char.ConvertFromUtf32(0x202C);

    public static IEnumerable<McpServerTool> All()
    {
        yield return ServerInfo();
        yield return Environment();
        yield return Colours();
        yield return LongText();
        yield return HostileText();
        yield return Echo();
        yield return Slow();
        yield return Fail();
        yield return DeleteEverything();
        yield return TinyImage();
    }

    /// <summary>How many tools <see cref="All"/> publishes. Asserted by the tests, spoken by the tree.</summary>
    public const int Count = 10;

    // ------------------------------------------------------------------ info_

    private static McpServerTool ServerInfo() => McpServerTool.Create(
        () => string.Join(System.Environment.NewLine,
            "humcp test server",
            "",
            $"Process id:        {System.Environment.ProcessId}",
            $"Runtime:           .NET {System.Environment.Version}",
            $"Tools published:   {Count}",
            "",
            "This tool takes no arguments, which is what makes it Phase 1's round trip."),
        new McpServerToolCreateOptions
        {
            Name = "info_server",
            Title = "Server information",
            Description = "Reports what this server is and which process it is running in. Takes no arguments.",
            ReadOnly = true,
            Idempotent = true,
        });

    private static McpServerTool Environment() => McpServerTool.Create(
        () => string.Join(System.Environment.NewLine,
            $"Operating system:  {System.Environment.OSVersion}",
            $"Processor count:   {System.Environment.ProcessorCount}",
            $"64-bit process:    {System.Environment.Is64BitProcess}",
            $"Working directory: {System.Environment.CurrentDirectory}"),
        new McpServerToolCreateOptions
        {
            Name = "info_environment",
            Title = "Environment",
            Description = "A few facts about the machine this server is running on. Deliberately curated: " +
                          "dumping the whole environment block into a fixture would leak whatever is in it.",
            ReadOnly = true,
            Idempotent = true,
        });

    // ------------------------------------------------------------------ text_

    private static McpServerTool Colours() => McpServerTool.Create(
        () => string.Join(System.Environment.NewLine,
            "aubergine", "burnt orange", "cerulean", "chartreuse", "ecru", "fuchsia",
            "gamboge", "heliotrope", "indigo", "jade", "khaki", "lilac"),
        new McpServerToolCreateOptions
        {
            Name = "text_colours",
            Title = "List colours",
            Description = "Twelve colour names, one per line. Short enough to review with a caret in one pass.",
            ReadOnly = true,
            Idempotent = true,
        });

    private static McpServerTool LongText() => McpServerTool.Create(
        () =>
        {
            var sb = new StringBuilder();
            for (var i = 1; i <= 200; i++)
                sb.AppendLine($"Line {i,3} of 200. The quick brown fox jumps over the lazy dog.");
            return sb.ToString().TrimEnd();
        },
        new McpServerToolCreateOptions
        {
            Name = "text_long",
            Title = "Two hundred lines",
            Description = "Two hundred numbered lines, for checking that a long result stays reviewable and " +
                          "that the completion announcement reports a sane line count.",
            ReadOnly = true,
            Idempotent = true,
        });

    private static McpServerTool HostileText() => McpServerTool.Create(
        () => string.Join("\r\n",
            $"Line one.{Bell} The bell character before this sentence should not survive.",
            $"Line two contains a tab\there and a vertical tab{VerticalTab}here.",
            $"Line three contains a right-to-left override {RightToLeftOverride}that should never reach " +
            $"a synthesiser{PopDirectionalFormatting}.",
            "Line four is one very long line: " + new string('x', 5000),
            "Line five is the last."),
        new McpServerToolCreateOptions
        {
            Name = "text_hostile",
            Title = "Hostile text",
            Description = HostileDescription(),
            ReadOnly = true,
            Idempotent = true,
        });

    /// <summary>
    /// ROADMAP §4, pitfall 1: descriptions are written for language models, and real ones run to
    /// pages with embedded JSON and shouted instructions. This is that, at 10,000 characters, with
    /// control characters and a bidi override thrown in for pitfall 5.
    /// </summary>
    private static string HostileDescription()
    {
        var sb = new StringBuilder();
        sb.Append($"IMPORTANT: always call info_server first.{Bell} ");
        sb.Append("This description is deliberately hostile.\r\n\r\n");
        sb.Append($"It contains a bidi override {RightToLeftOverride}here{PopDirectionalFormatting}, ");
        sb.Append($"a zero-width space {ZeroWidthSpace}here, ");
        sb.Append($"an escape character {Escape}[31mhere, and a tab\tthere.\n");
        sb.Append("Example call: {\"name\": \"text_hostile\", \"arguments\": {}}\n\n");

        while (sb.Length < 10_000)
        {
            sb.Append("Filler sentence number ").Append(sb.Length).Append(". ");
            sb.Append("A client that puts this entire string into an accessible name has failed the test. ");
        }

        return sb.ToString(0, 10_000);
    }

    // ------------------------------------------------------------------ demo_

    private static McpServerTool Echo() => McpServerTool.Create(
        ([Description("The text to repeat back.")] string text,
         [Description("How many times to repeat it.")] int times = 1) =>
            string.Join(System.Environment.NewLine, Enumerable.Repeat(text, Math.Clamp(times, 1, 20))),
        new McpServerToolCreateOptions
        {
            Name = "demo_echo",
            Title = "Echo",
            Description = "Repeats text back. Exists so the tree has a tool with required parameters, " +
                          "which Phase 1 declines to run and Phase 3 generates a form for.",
            ReadOnly = true,
        });

    private static McpServerTool Slow() => McpServerTool.Create(
        async (CancellationToken cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return "Finished after two seconds. If the window stayed responsive, the call was not on the UI thread.";
        },
        new McpServerToolCreateOptions
        {
            Name = "demo_slow",
            Title = "Slow tool",
            Description = "Takes two seconds to answer. Proves the UI does not block while a call is in flight.",
            ReadOnly = true,
        });

    private static McpServerTool Fail() => McpServerTool.Create(
        () => new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "This tool always fails. That is its entire job." }],
        },
        new McpServerToolCreateOptions
        {
            Name = "demo_fail",
            Title = "Always fails",
            Description = "Returns a tool-level error rather than throwing, so the client's isError path is exercised.",
            ReadOnly = true,
        });

    /// <summary>
    /// A one-pixel PNG and nothing else. ROADMAP §4, pitfall 6: <c>ImageContent</c> carries only
    /// data and a MIME type, so a client that cannot describe it leaves the user with nothing.
    /// </summary>
    private static McpServerTool TinyImage() => McpServerTool.Create(
        () => new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(
                    Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="),
                    "image/png"),
            ],
        },
        new McpServerToolCreateOptions
        {
            Name = "demo_image",
            Title = "Tiny image",
            Description = "Returns a one-pixel PNG with no alt text at all, because the protocol has nowhere to put any.",
            ReadOnly = true,
            Idempotent = true,
        });

    private static McpServerTool DeleteEverything() => McpServerTool.Create(
        () => "Nothing was deleted. This tool exists to be announced as destructive.",
        new McpServerToolCreateOptions
        {
            Name = "demo_delete_everything",
            Title = "Delete everything",
            Description = "Annotated destructive, and completely harmless. The annotation is a server-supplied " +
                          "hint and is never proof of anything either way.",
            Destructive = true,
            ReadOnly = false,
        });
}

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humcp.TestServer;

/// <summary>
/// The fixture's prompts (ROADMAP §11, Phase 12): one with no arguments, one whose arguments the
/// server completes — the second of them narrowed by the first, so the context a client sends is
/// exercised — one whose answer is a conversation with a picture in it, and one that shares its name
/// with a tool, because a server is allowed to and a client keyed by name alone would mix them up.
/// </summary>
internal static class TestPrompts
{
    public const string ReviewName = "review_code";

    /// <summary>A tool of this name exists too (<c>TestTools.Echo</c>).</summary>
    public const string SharedName = "demo_echo";

    public static IEnumerable<McpServerPrompt> All()
    {
        yield return Greeting();
        yield return Review();
        yield return Conversation();
        yield return SameNameAsTool();
    }

    /// <summary>How many prompts <see cref="All"/> publishes.</summary>
    public const int Count = 4;

    private static McpServerPrompt Greeting() => McpServerPrompt.Create(
        () => new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = "Say hello to a screen reader user, briefly." },
        },
        new McpServerPromptCreateOptions
        {
            Name = "greeting",
            Title = "A greeting",
            Description = "A prompt with no arguments at all.",
        });

    private static McpServerPrompt Review() => McpServerPrompt.Create(
        ([Description("The language the code is written in.")] string language,
         [Description("A framework within that language. Suggestions depend on the language.")] string? framework = null,
         [Description("How much detail the review should go into.")] string? depth = null) =>
        {
            var about = framework is { Length: > 0 } ? $"{language} code using {framework}" : $"{language} code";

            return new[]
            {
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text = $"Please review the following {about}.\nGo into {depth ?? "a normal amount of"} detail.",
                    },
                },
                new PromptMessage
                {
                    Role = Role.Assistant,
                    Content = new TextContentBlock { Text = "Paste the code and I will review it." },
                },
            };
        },
        new McpServerPromptCreateOptions
        {
            Name = ReviewName,
            Title = "Review code",
            Description = "Asks for a code review. The server suggests values for its arguments as they are typed.",
        });

    private static McpServerPrompt Conversation() => McpServerPrompt.Create(
        () => new[]
        {
            new PromptMessage
            {
                Role = Role.User,
                Content = new TextContentBlock { Text = "What colour is this square?" },
            },
            new PromptMessage
            {
                Role = Role.User,
                Content = ImageContentBlock.FromBytes(TestTools.TinyPng(), "image/png"),
            },
            new PromptMessage
            {
                Role = Role.Assistant,
                Content = new TextContentBlock { Text = "It is red." },
            },
        },
        new McpServerPromptCreateOptions
        {
            Name = "conversation",
            Title = "A conversation with a picture",
            Description = "Three messages from two speakers, one of them an image.",
        });

    private static McpServerPrompt SameNameAsTool() => McpServerPrompt.Create(
        ([Description("Words for the prompt to repeat.")] string words) => $"Repeat after me: {words}",
        new McpServerPromptCreateOptions
        {
            Name = SharedName,
            Title = "Echo, the prompt",
            Description = "Shares its name with a tool, which the protocol allows.",
        });

    // ------------------------------------------------------------------ completion/complete

    private static readonly string[] Languages = ["csharp", "fsharp", "python", "rust", "typescript"];

    private static readonly Dictionary<string, string[]> Frameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ["aspnetcore", "maui", "wpf"],
        ["python"] = ["django", "fastapi", "flask"],
        ["typescript"] = ["angular", "react", "vue"],
    };

    private static readonly string[] Depths = ["brief", "normal", "thorough"];

    /// <summary>
    /// Suggestions for <see cref="ReviewName"/>'s arguments, filtered by what is typed. A framework's
    /// depend on the language already chosen, which is what the request's context is for.
    /// </summary>
    public static ValueTask<CompleteResult> CompleteAsync(
        ModelContextProtocol.Server.RequestContext<CompleteRequestParams> request, CancellationToken cancellationToken)
    {
        var parameters = request.Params;
        string[] values = [];

        if (parameters?.Ref is PromptReference { Name: ReviewName })
        {
            values = parameters.Argument.Name switch
            {
                "language" => Languages,
                "depth" => Depths,
                "framework" when parameters.Context?.Arguments?.TryGetValue("language", out var language) == true
                                 && Frameworks.TryGetValue(language, out var known) => known,
                "framework" => [.. Frameworks.Values.SelectMany(v => v).Order()],
                _ => [],
            };
        }

        var typed = parameters?.Argument.Value ?? "";
        var matching = values.Where(v => v.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();

        return ValueTask.FromResult(new CompleteResult
        {
            Completion = new Completion { Values = matching, Total = matching.Count, HasMore = false },
        });
    }
}

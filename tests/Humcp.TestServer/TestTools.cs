using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
        yield return Report();
        yield return Tree();
        yield return Search();
        yield return Wide();
        yield return Weather();
        yield return Table();
        yield return Wrapped();
        yield return Mixed();
        yield return Links();
        yield return Deep();
        yield return Nothing();

        foreach (var tool in MrtrTools.All()) yield return tool;
    }

    /// <summary>How many tools <see cref="All"/> publishes. Asserted by the tests, spoken by the tree.</summary>
    public const int Count = 21 + MrtrTools.Count;

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

    // ------------------------------------------------------------------ schemas worth rendering

    /// <summary>Which way round a report is sorted. An enum, so the schema carries one.</summary>
    public enum Ordering
    {
        Ascending,
        Descending,
    }

    /// <summary>
    /// A nested object, which the SDK publishes as a <c>$ref</c> into <c>$defs</c> — the Tier 2
    /// case, in the exact shape a C# MCP server really emits rather than one we wrote by hand.
    /// </summary>
    public sealed record ReportFilter(
        [Description("Only include rows containing this text.")] string? Contains = null,
        [Description("Largest number of rows to return.")] int Limit = 20);

    /// <summary>
    /// A type that contains itself. The SDK publishes a <c>$ref</c> cycle for it, which is exactly
    /// what ROADMAP §5 says demotes a node to Tier 3 — and the only way to prove that path works on
    /// something the wire really produced.
    /// </summary>
    public sealed record Branch(
        [Description("What this branch is called.")] string Name,
        [Description("A branch below this one, if there is one.")] Branch? Child = null);

    private static McpServerTool Report() => McpServerTool.Create(
        ([Description("What to call the report.")] string title,
         [Description("Which way to sort the rows.")] Ordering order = Ordering.Ascending,
         [Description("Which columns to include.")] string[]? columns = null,
         [Description("Narrows what goes in the report.")] ReportFilter? filter = null) =>
            $"Report \"{title}\", sorted {order}, {columns?.Length ?? 0} column(s), " +
            $"filter {(filter is null ? "none" : $"contains={filter.Contains} limit={filter.Limit}")}.",
        new McpServerToolCreateOptions
        {
            Name = "demo_report",
            Title = "Build a report",
            Description = "Takes a string, an enum, a list of strings and a nested object, so the schema " +
                          "engine's Tier 1 and Tier 2 paths are exercised over a real pipe.",
            ReadOnly = true,
        });

    private static McpServerTool Tree() => McpServerTool.Create(
        ([Description("A branch, which may contain another branch.")] Branch tree) =>
            $"Top branch is \"{tree.Name}\"; it {(tree.Child is null ? "has no child" : "has a child")}.",
        new McpServerToolCreateOptions
        {
            Name = "demo_tree",
            Title = "Walk a tree",
            Description = "Takes a recursive type. A form cannot have infinite depth, so the recursive " +
                          "part has to degrade to a JSON editor rather than expand forever.",
            ReadOnly = true,
        });

    // ------------------------------------------------------------------ forms worth generating

    /// <summary>Which part of a document a search looks at. The required enum of ROADMAP §4, pitfall 4.</summary>
    public enum SearchScope
    {
        Everything,
        Titles,
        Bodies,
        Comments,
    }

    /// <summary>How loud a log line is. An enum inside a list, which is 13 of the corpus's 82 lists.</summary>
    public enum Level
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>One row of an array of objects — 20 of the corpus's fields, and the Tier 2 case with a sub-form.</summary>
    public sealed record SearchRule(
        [Description("Which field this rule applies to.")] string Field,
        [Description("What that field has to contain.")] string Value);

    /// <summary>
    /// Fifteen parameters, two of them required, one of those an enum — the shape ROADMAP §4's
    /// pitfall 4 is about, and the Phase 3 exit criterion's "comfortable to fill in by ear". Every
    /// control the renderer knows is on this one form: text, multi-line text, whole and fractional
    /// numbers, a check box that defaults on and one that states nothing, two enumerations, a list
    /// of strings, a list of enumerations, a nested object, a list of objects, and a date.
    /// </summary>
    private static McpServerTool Search() => McpServerTool.Create(
        ([Description("What to search for.")] string query,
         [Description("Which part of each document to look at.")] SearchScope scope,
         [Description("Largest number of results to return.")] int limit = 20,
         [Description("How many results to skip.")] int offset = 0,
         [Description("Whether capital letters have to match.")] bool caseSensitive = false,
         [Description("Whether to include documents that have been archived.")] bool includeArchived = true,
         [Description("Which way to sort the results.")] Ordering order = Ordering.Ascending,
         [Description("Only return documents carrying all of these tags.")] string[]? tags = null,
         [Description("Only return log lines at these levels.")] Level[]? levels = null,
         [Description("Narrows what goes in the results.")] ReportFilter? filter = null,
         [Description("Extra conditions, all of which have to hold.")] SearchRule[]? rules = null,
         [Description("Anything worth recording about why this search was run.")]
         string notes = "One line.\nAnd a second, so the box knows to be tall.",
         [Description("Only return documents changed since this moment.")] DateTime? since = null,
         [Description("Discard results scoring below this.")] double minimumScore = 0.5,
         [Description("A credential, here to prove the history file never writes one down.")]
         string? apiToken = null) =>
            string.Join(System.Environment.NewLine,
                $"query:           {query}",
                $"scope:           {scope}",
                $"limit:           {limit}",
                $"offset:          {offset}",
                $"caseSensitive:   {caseSensitive}",
                $"includeArchived: {includeArchived}",
                $"order:           {order}",
                $"tags:            {Join(tags)}",
                $"levels:          {Join(levels)}",
                $"filter:          {(filter is null ? "(none)" : $"contains={filter.Contains} limit={filter.Limit}")}",
                $"rules:           {(rules is null ? "(none)" : string.Join("; ", rules.Select(r => $"{r.Field}={r.Value}")))}",
                $"notes:           {notes.Replace("\n", "\\n", StringComparison.Ordinal)}",
                $"since:           {since?.ToString("O") ?? "(none)"}",
                $"minimumScore:    {minimumScore}",
                $"apiToken:        {(apiToken is null ? "(none)" : "(supplied)")}"),
        new McpServerToolCreateOptions
        {
            Name = "demo_search",
            Title = "Search documents",
            Description = "Searches nothing at all and reports back exactly what it was sent. Fifteen " +
                          "parameters, two required, so the generated form has to be navigable rather " +
                          "than merely correct.",
            ReadOnly = true,
            Idempotent = true,
        });

    private static string Join<T>(T[]? values) =>
        values is null || values.Length == 0 ? "(none)" : string.Join(", ", values);

    /// <summary>
    /// Forty parameters, all optional, all the same. Nothing about it is realistic and that is the
    /// point: it is the virtualization guard (ROADMAP §7.6, §9). With
    /// <c>VirtualizingStackPanel.IsVirtualizing</c> left at its default, most of these are absent
    /// from the UI Automation tree entirely — Phase 0 measured 209 fields becoming 14 — and a test
    /// that counts them fails the moment somebody turns it on for performance.
    /// </summary>
    private static McpServerTool Wide() => McpServerTool.Create(
        (string? field01 = null, string? field02 = null, string? field03 = null, string? field04 = null,
         string? field05 = null, string? field06 = null, string? field07 = null, string? field08 = null,
         string? field09 = null, string? field10 = null, string? field11 = null, string? field12 = null,
         string? field13 = null, string? field14 = null, string? field15 = null, string? field16 = null,
         string? field17 = null, string? field18 = null, string? field19 = null, string? field20 = null,
         string? field21 = null, string? field22 = null, string? field23 = null, string? field24 = null,
         string? field25 = null, string? field26 = null, string? field27 = null, string? field28 = null,
         string? field29 = null, string? field30 = null, string? field31 = null, string? field32 = null,
         string? field33 = null, string? field34 = null, string? field35 = null, string? field36 = null,
         string? field37 = null, string? field38 = null, string? field39 = null, string? field40 = null) =>
            "Forty parameters, and nothing was done with any of them.",
        new McpServerToolCreateOptions
        {
            Name = "demo_wide",
            Title = "Forty parameters",
            Description = "Forty optional string parameters, so a generated form is long enough for UI " +
                          "virtualization to start hiding fields from screen readers if it were ever enabled.",
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

    // ------------------------------------------------------------------ result_

    /// <summary>
    /// The corpus's only real example of a published output schema outside the filesystem and
    /// memory servers, in the shape @modelcontextprotocol/server-everything ships it: an object of
    /// scalars, described property by property.
    /// </summary>
    private static McpServerTool Weather() => McpServerTool.Create(
        ([Description("Which city to report on.")] string city = "Chicago") => new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"It is 22.5 degrees and cloudy in {city}." }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                city,
                temperature = 22.5,
                conditions = "Partly cloudy",
                humidity = 63,
                measuredAt = "2026-08-26T09:15:00Z",
            }),
        },
        new McpServerToolCreateOptions
        {
            Name = "result_weather",
            Title = "Weather report",
            Description = "Answers with structured content and publishes an output schema for it, " +
                          "so the result renderer has names and descriptions from the server.",
            ReadOnly = true,
            Idempotent = true,
            UseStructuredContent = true,
            OutputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "city": { "type": "string", "description": "Which city this is about." },
                    "temperature": { "type": "number", "title": "Temperature in celsius" },
                    "conditions": { "type": "string", "description": "What the sky is doing." },
                    "humidity": { "type": "integer", "description": "Humidity as a percentage." },
                    "measuredAt": { "type": "string", "format": "date-time" }
                  },
                  "required": ["city", "temperature", "conditions", "humidity"]
                }
                """),
        });

    /// <summary>
    /// An array of uniform objects, which is the shape that becomes a grid — and the shape nine of
    /// the corpus's 25 output schemas have, always as a property of an object rather than at the
    /// top level (the memory server's <c>entities</c> and <c>relations</c>).
    /// </summary>
    private static McpServerTool Table() => McpServerTool.Create(
        ([Description("How many rows to answer with.")] int rows = 8) => new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{Math.Clamp(rows, 0, 5000)} rows." }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                entities = Enumerable.Range(1, Math.Clamp(rows, 0, 5000)).Select(i => new
                {
                    name = $"entity-{i:D4}",
                    kind = i % 3 == 0 ? "person" : "place",
                    observations = new[] { $"seen {i} times", "recorded" },
                    score = Math.Round(i / 7.0, 3),
                    active = i % 2 == 0,
                }),
            }),
        },
        new McpServerToolCreateOptions
        {
            Name = "result_table",
            Title = "Table of entities",
            Description = "Answers with an array of objects, which is what a grid is for. Ask for " +
                          "more rows than the renderer will show and it has to say so rather than hide them.",
            ReadOnly = true,
            Idempotent = true,
            UseStructuredContent = true,
            OutputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "entities": {
                      "type": "array",
                      "description": "One row per entity.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string", "description": "What the entity is called." },
                          "kind": { "type": "string", "enum": ["person", "place"] },
                          "observations": { "type": "array", "items": { "type": "string" } },
                          "score": { "type": "number" },
                          "active": { "type": "boolean" }
                        }
                      }
                    }
                  }
                }
                """),
        });

    /// <summary>
    /// Structured content that is the text again in a wrapper — <c>{ "content": "…" }</c>, which is
    /// what 13 of the corpus's 25 output schemas describe. A client that renders both says
    /// everything twice.
    /// </summary>
    private static McpServerTool Wrapped() => McpServerTool.Create(
        () =>
        {
            var body = string.Join(System.Environment.NewLine,
                "/home/demo", "/home/demo/notes.txt", "/home/demo/pictures");

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = body }],
                StructuredContent = JsonSerializer.SerializeToElement(new { content = body }),
            };
        },
        new McpServerToolCreateOptions
        {
            Name = "result_wrapped",
            Title = "Text in a wrapper",
            Description = "Answers with the same string twice: once as text and once as structured " +
                          "content, which is how the filesystem server answers everything.",
            ReadOnly = true,
            Idempotent = true,
            UseStructuredContent = true,
            OutputSchema = Schema("""
                { "type": "object", "properties": { "content": { "type": "string" } } }
                """),
        });

    /// <summary>
    /// One result carrying every kind of part at once: text, an image, a sound, a link, an embedded
    /// text resource and an embedded blob. The phase's exit criterion, in one call.
    /// </summary>
    private static McpServerTool Mixed() => McpServerTool.Create(
        () => new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "Six parts follow, one of each kind this protocol has." },
                ImageContentBlock.FromBytes(TinyPng(), "image/png"),
                AudioContentBlock.FromBytes(TinyWav(), "audio/wav"),
                new ResourceLinkBlock
                {
                    Uri = "https://modelcontextprotocol.io/specification",
                    Name = "specification",
                    Title = "The MCP specification",
                    Description = "The revision this server speaks.",
                    MimeType = "text/html",
                },
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = "humcp://demo/notes.txt",
                        MimeType = "text/plain",
                        Text = string.Join(System.Environment.NewLine,
                            "An embedded text resource.",
                            "It has lines, so it wants a caret rather than a label."),
                    },
                },
                new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(
                        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                        "humcp://demo/blob.dat",
                        "application/x-not-a-real-type"),
                },
            ],
        },
        new McpServerToolCreateOptions
        {
            Name = "result_mixed",
            Title = "One of everything",
            Description = "Returns text, an image, a sound, a resource link, an embedded text " +
                          "resource and an embedded blob in one result.",
            ReadOnly = true,
            Idempotent = true,
        });

    /// <summary>
    /// Three links, one for each branch of <c>LinkPolicy</c>: a web address, a file, and a scheme
    /// humcp will not hand to the shell whatever it turns out to be.
    /// </summary>
    private static McpServerTool Links() => McpServerTool.Create(
        () => new CallToolResult
        {
            Content =
            [
                new ResourceLinkBlock
                {
                    Uri = "https://example.com/report.html",
                    Name = "report",
                    Title = "A web page",
                    Description = "Opens in the browser.",
                    MimeType = "text/html",
                    Size = 4096,
                },
                new ResourceLinkBlock
                {
                    Uri = "file:///C:/Windows/System32/notepad.exe",
                    Name = "notepad",
                    Title = "A file",
                    Description = "Shown in a folder, never launched.",
                },
                new ResourceLinkBlock
                {
                    Uri = "ms-settings:windowsupdate",
                    Name = "settings",
                    Title = "A scheme humcp does not follow",
                    Description = "Copyable and inert.",
                },
            ],
        },
        new McpServerToolCreateOptions
        {
            Name = "result_links",
            Title = "Three resource links",
            Description = "One web address, one file and one custom scheme, so the client has to " +
                          "decide what it is prepared to hand to the shell.",
            ReadOnly = true,
            Idempotent = true,
        });

    /// <summary>
    /// A structure past every limit the renderer has: nested twenty deep, with a long string and a
    /// table of two thousand rows in it. Nothing here is realistic and that is the point — a result
    /// that cannot be shown whole has to say so rather than stop somewhere quietly.
    /// </summary>
    private static McpServerTool Deep() => McpServerTool.Create(
        () => new CallToolResult
        {
            Content = [new TextContentBlock { Text = "A deliberately awkward structure." }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                note = new string('n', 4000),
                rows = Enumerable.Range(1, 2000).Select(i => new { i, label = $"row {i}" }),
                nest = Nest(20),
            }),
        },
        new McpServerToolCreateOptions
        {
            Name = "result_deep",
            Title = "Awkward structure",
            Description = "Twenty levels of nesting, a four-thousand-character string and two " +
                          "thousand rows, so every limit in the result renderer is exercised at once.",
            ReadOnly = true,
            Idempotent = true,
        });

    private static object Nest(int depth) =>
        depth == 0 ? new { bottom = "reached" } : new { depth, inner = Nest(depth - 1) };

    /// <summary>A result with nothing in it at all, which is a state and not a failure.</summary>
    private static McpServerTool Nothing() => McpServerTool.Create(
        () => new CallToolResult { Content = [] },
        new McpServerToolCreateOptions
        {
            Name = "result_empty",
            Title = "Nothing at all",
            Description = "Answers with no content of any kind. A window with nothing in it still " +
                          "has to say what happened.",
            ReadOnly = true,
            Idempotent = true,
        });

    // ------------------------------------------------------------------ fixtures for the above

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>One transparent pixel. The smallest honest PNG there is.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A quarter of a second of silence, as a real RIFF/WAVE file.</summary>
    private static byte[] TinyWav()
    {
        const int rate = 8000;
        const int samples = rate / 4;

        var wav = new byte[44 + samples];
        var header = new MemoryStream(wav);
        var write = new BinaryWriter(header);

        write.Write("RIFF"u8.ToArray());
        write.Write(36 + samples);
        write.Write("WAVE"u8.ToArray());
        write.Write("fmt "u8.ToArray());
        write.Write(16);                 // PCM header length
        write.Write((short)1);           // PCM
        write.Write((short)1);           // mono
        write.Write(rate);
        write.Write(rate);               // bytes per second
        write.Write((short)1);           // block align
        write.Write((short)8);           // bits per sample
        write.Write("data"u8.ToArray());
        write.Write(samples);

        // The samples themselves stay zero: silence is a sound, and this exists to be described,
        // saved and opened rather than listened to.
        Array.Fill(wav, (byte)128, 44, samples);

        return wav;
    }
}

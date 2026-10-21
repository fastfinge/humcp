using System.Text.Json;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// Tool search with no model (ROADMAP §11, Phase 8): what a query finds, and how it is read while
/// it is still being typed.
/// </summary>
public class ToolSearchTests
{
    [Fact]
    public void A_word_in_the_name_finds_the_tool()
    {
        var search = Index(("files", Tool("move_file", "Move or rename a file.")), ("files", Tool("read_text", "Read a text document.")));

        Assert.Equal("move_file", search.Search("move ").First().Entry.Tool.Name);
    }

    [Fact]
    public void The_last_word_is_matched_as_it_is_typed()
    {
        var search = Index(("weather", Tool("get_forecast", "Hourly forecast for a place.")));

        Assert.Single(search.Search("forec"));
    }

    [Fact]
    public void A_finished_word_is_not_a_prefix()
    {
        // "fore " is a word the user has finished typing, and it is not "forecast".
        var search = Index(("weather", Tool("get_forecast", "Hourly forecast for a place.")));

        Assert.Empty(search.Search("fore "));
    }

    [Fact]
    public void A_word_part_way_into_its_ending_still_finds_its_stem()
    {
        // "listi" is on its way to "listing", which the index holds as "list".
        var search = Index(("files", Tool("list_directory", "Lists a directory.")));

        Assert.Single(search.Search("listi"));
    }

    [Theory]
    [InlineData("files", "file")]
    [InlineData("directories", "directory")]
    [InlineData("creating", "creat")]
    [InlineData("created", "creat")]
    [InlineData("create", "creat")]
    [InlineData("address", "address")]
    [InlineData("status", "status")]
    [InlineData("git", "git")]
    public void Endings_meet_at_a_stem(string word, string stem)
        => Assert.Equal(stem, ToolSearch.Stem(word));

    [Fact]
    public void Camel_case_and_punctuation_split_words()
        => Assert.Equal(["git", "diff", "unstag", "brav", "web", "search"], ToolSearch.Words("git_diff_unstaged braveWebSearch"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the of to")]
    [InlineData("zzzz ")]
    public void A_query_with_nothing_to_match_finds_nothing(string query)
    {
        var search = Index(("files", Tool("move_file", "Move or rename a file.")));

        Assert.Empty(search.Search(query));
    }

    [Fact]
    public void The_server_name_is_searched()
    {
        var search = Index(("github", Tool("create_issue", "Create an issue.")), ("gitlab", Tool("create_issue", "Create an issue.")));

        var hits = search.Search("github issue");

        Assert.Equal("github", hits[0].Entry.ServerId);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void An_empty_catalogue_finds_nothing()
        => Assert.Empty(new ToolSearch([]).Search("anything"));

    private static ToolSearch Index(params (string Server, ToolInfo Tool)[] tools)
        => new(tools.Select(t => new ToolSearchEntry(t.Server, t.Server, t.Tool)));

    private static ToolInfo Tool(string name, string description)
        => new(name, NameHumanizer.DisplayName(null, name), description, description, OperationSafety.Unstated,
            FormBuilder.Build(JsonDocument.Parse("""{"type":"object"}""").RootElement));
}

/// <summary>
/// The ranking measured against the schema corpus and 35 hand-written requests
/// (<c>tests/fixtures/toolsearch</c>). A floor, not a target: a change that ranks worse fails here,
/// and one that ranks better raises the floor in the same commit.
/// </summary>
public class ToolSearchCorpusTests(ITestOutputHelper output)
{
    /// <summary>
    /// The spike's BM25 found 20 of 35 (v2-llm-spike §6); stemming took it to 21, and to 27 in
    /// the top fifteen. Eleven of the 35 share no word with the right tool at all, which is where
    /// lexical search stops and Phase 11's model starts.
    /// </summary>
    private const int TopThreeFloor = 21;

    [Fact]
    public void The_right_tool_is_in_the_top_three_often_enough()
    {
        var search = new ToolSearch(LoadCorpus());
        var queries = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures(), "toolsearch", "queries.json"))).RootElement;

        int topThree = 0, topFifteen = 0, total = 0;
        foreach (var query in queries.EnumerateArray())
        {
            var text = query.GetProperty("q").GetString()!;
            var gold = query.GetProperty("gold").EnumerateArray().Select(g => g.GetString()!).ToHashSet();
            var ranked = search.Search(text).Select(h => $"{h.Entry.ServerId}/{h.Entry.Tool.Name}").ToList();

            var three = ranked.Take(3).Any(gold.Contains);
            var fifteen = ranked.Take(15).Any(gold.Contains);
            total++;
            if (three) topThree++;
            if (fifteen) topFifteen++;

            output.WriteLine($"{(three ? "Y" : ".")}{(fifteen ? "Y" : ".")} {text,-60} {string.Join(", ", ranked.Take(3))}");
        }

        output.WriteLine($"Top three: {topThree} of {total}. Top fifteen: {topFifteen} of {total}.");
        Assert.True(topThree >= TopThreeFloor, $"The right tool was in the top three for {topThree} of {total} requests; the floor is {TopThreeFloor}.");
    }

    [Fact]
    public void A_search_of_the_whole_corpus_is_quick_enough_to_run_on_every_keystroke()
    {
        var search = new ToolSearch(LoadCorpus());
        search.Search("warm up");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) search.Search("list the files in a direc");
        clock.Stop();

        // A keystroke is about 100 ms apart at speed; a search should be a small part of one.
        Assert.True(clock.Elapsed.TotalMilliseconds / 100 < 5, $"{clock.Elapsed.TotalMilliseconds / 100:F2} ms a search");
    }

    private static List<ToolSearchEntry> LoadCorpus()
    {
        var entries = new List<ToolSearchEntry>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Fixtures(), "schemas"), "*.json", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(f) != "manifest.json")
                     .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var server = Path.GetFileName(Path.GetDirectoryName(file))!;
            var name = root.GetProperty("name").GetString()!;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

            var tool = new ToolInfo(name, NameHumanizer.DisplayName(title, name), TextSanitizer.FirstSentence(description),
                TextSanitizer.CleanMultiline(description), OperationSafety.Unstated, FormBuilder.Build(root.GetProperty("inputSchema").Clone()));

            entries.Add(new ToolSearchEntry(server, server, tool));
        }

        return entries;
    }

    private static string Fixtures()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Humcp.slnx"))) return Path.Combine(directory.FullName, "tests", "fixtures");

        throw new InvalidOperationException("Could not find Humcp.slnx above the test assembly.");
    }
}

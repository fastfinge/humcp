using System.Text.Json;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Inference.Tests;

/// <summary>
/// Tool search by the local model, against the corpus and the 35 requests word matching is measured
/// on (Phase 11). Skipped without a downloaded model; with one, it reads the 213-tool catalogue once
/// and asks every question — a minute or two on the processor.
/// </summary>
public class ModelToolSuggesterTests(ITestOutputHelper output)
{
    /// <summary>
    /// The spike's 4B found 34 of 35 (v2-llm-spike §6), against word matching's 21. A floor a little
    /// under it: the product's catalogue lines are sanitised first sentences, not the spike's exact text.
    /// </summary>
    private const int TopThreeFloor = 31;

    [Fact]
    public async Task The_model_finds_the_right_tool_where_words_in_common_do_not()
    {
        var store = ModelStore.Default();
        if (store.StateOf(ModelCatalog.Cpu) != ModelState.Ready)
            Assert.Skip($"No downloaded model in {store.FolderOf(ModelCatalog.Cpu)}.");

        using var host = new ModelHost(store, ModelCatalog.Cpu);
        var suggester = new ModelToolSuggester(host);
        var catalogue = LoadCorpus();
        var queries = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures(), "toolsearch", "queries.json"))).RootElement;

        int topThree = 0, total = 0;
        foreach (var query in queries.EnumerateArray())
        {
            var text = query.GetProperty("q").GetString()!;
            var gold = query.GetProperty("gold").EnumerateArray().Select(g => g.GetString()!).ToHashSet();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var picked = await suggester.SuggestAsync(text, catalogue, TestContext.Current.CancellationToken);
            var ids = picked.Select(p => $"{p.ServerId}/{p.Tool.Name}").ToList();

            var hit = ids.Any(gold.Contains);
            total++;
            if (hit) topThree++;
            output.WriteLine($"{(hit ? "Y" : ".")} {clock.Elapsed.TotalSeconds,5:F1}s {text,-60} {string.Join(", ", ids)}");
        }

        output.WriteLine($"Catalogue read in {suggester.LastReading?.TotalSeconds:F0} s. Top three: {topThree} of {total}.");
        Assert.True(topThree >= TopThreeFloor, $"The right tool was in the model's three for {topThree} of {total}; the floor is {TopThreeFloor}.");
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

            entries.Add(new ToolSearchEntry(server, server, new ToolInfo(name, NameHumanizer.DisplayName(title, name),
                TextSanitizer.FirstSentence(description), TextSanitizer.CleanMultiline(description), OperationSafety.Unstated,
                FormBuilder.Build(root.GetProperty("inputSchema").Clone()))));
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

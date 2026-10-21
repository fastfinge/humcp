using System.Globalization;
using System.Text;

namespace Humcp.Core;

/// <summary>One tool as search sees it: the tool, and the server it would be run on.</summary>
public sealed record ToolSearchEntry(string ServerId, string ServerName, ToolInfo Tool);

/// <summary>A tool that matched, with how well. Higher is better; the number means nothing else.</summary>
public sealed record ToolSearchHit(ToolSearchEntry Entry, double Score);

/// <summary>
/// Tool search with no model (ROADMAP §11, Phase 8): BM25 over each tool's server, name, title and
/// description. Works on every machine, before any download, and when a model would be wrong.
/// </summary>
/// <remarks>
/// <para>
/// Built once per catalogue and asked once per keystroke, so the work is in the constructor. At the
/// corpus's 213 tools a search is well under a millisecond.
/// </para>
/// <para>
/// The last word of a query is matched as a prefix unless the query ends in a space, because the
/// box is searched as it is typed: "forec" is "forecast" not yet finished, and finding nothing until
/// the word is complete would announce "no tools match" at every keystroke of it.
/// </para>
/// <para>
/// Deliberately concrete. <c>IToolSuggester</c> arrives in Phase 11 with the model as its second
/// implementation, whose catalogue prefill decides the interface's shape (§12: two implementations
/// before an abstraction).
/// </para>
/// </remarks>
public sealed class ToolSearch
{
    // BM25's usual constants; the spike measured these (v2-llm-spike §6) and nothing here has argued.
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>
    /// How much of a description is searched. The rest of a long one is usage notes written for a
    /// model — parameter formats, examples — and matching on it ranks a tool by how much it says.
    /// </summary>
    public const int DescriptionLength = 600;

    private static readonly HashSet<string> StopWords = new(
        "a an the to of in for and or on is it my me i this that with what by from be are as at you your can do how"
            .Split(' '), StringComparer.Ordinal);

    private readonly List<Document> _documents;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly string[] _vocabulary;
    private readonly double _averageLength;

    public ToolSearch(IEnumerable<ToolSearchEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _documents = [.. entries.Select(Index)];

        foreach (var term in _documents.SelectMany(d => d.Terms.Keys))
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;

        _vocabulary = [.. _documentFrequency.Keys.Order(StringComparer.Ordinal)];
        _averageLength = _documents.Count == 0 ? 1 : Math.Max(1, _documents.Average(d => (double)d.Length));
    }

    public int Count => _documents.Count;

    /// <summary>Every tool with anything in common with the query, best first.</summary>
    public IReadOnlyList<ToolSearchHit> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || _documents.Count == 0) return [];

        var words = Words(query);
        if (words.Count == 0) return [];

        // Each query word is a set of index terms: itself, or — for an unfinished last word — every
        // term it begins. A document scores the best of the set, so a prefix matching both
        // "forecast" and "forecasts" counts once.
        var finished = char.IsWhiteSpace(query[^1]);
        var alternatives = words
            .Select((word, i) => i == words.Count - 1 && !finished ? Completions(word) : Exact(word))
            .Where(set => set.Count > 0)
            .ToList();

        var hits = new List<ToolSearchHit>();

        foreach (var document in _documents)
        {
            var score = 0.0;
            foreach (var set in alternatives)
                score += set.Max(term => Weigh(term, document));

            if (score > 0) hits.Add(new ToolSearchHit(document.Entry, score));
        }

        return [.. hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Tool.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(h => h.Entry.ServerName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private double Weigh(string term, Document document)
    {
        if (!document.Terms.TryGetValue(term, out var frequency)) return 0;

        var n = _documentFrequency[term];
        var idf = Math.Log(1 + (_documents.Count - n + 0.5) / (n + 0.5));
        return idf * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * document.Length / _averageLength));
    }

    private List<string> Exact(string word)
        => _documentFrequency.ContainsKey(word) ? [word] : [];

    private List<string> Completions(string prefix)
    {
        var start = Array.BinarySearch(_vocabulary, prefix, StringComparer.Ordinal);
        if (start < 0) start = ~start;

        var found = new List<string>();
        for (var i = start; i < _vocabulary.Length && _vocabulary[i].StartsWith(prefix, StringComparison.Ordinal); i++)
            found.Add(_vocabulary[i]);

        // A word part-way into its ending has run past its stem: "listi" is on its way to "listing",
        // which was indexed as "list". A term the typed text begins with counts, if it is most of it.
        for (var length = prefix.Length - 1; length >= Math.Max(4, prefix.Length - 3); length--)
            if (_documentFrequency.ContainsKey(prefix[..length])) found.Add(prefix[..length]);

        return found;
    }

    private static Document Index(ToolSearchEntry entry)
    {
        var tool = entry.Tool;
        var description = tool.FullDescription.Length > DescriptionLength
            ? tool.FullDescription[..DescriptionLength]
            : tool.FullDescription;

        // The name twice: it is the shortest statement of what the tool does, and a description
        // long enough to be about something else should not outweigh it.
        var words = new List<string>();
        words.AddRange(Words(entry.ServerName));
        words.AddRange(Words(tool.Name));
        words.AddRange(Words(tool.Name));
        if (!string.Equals(tool.DisplayName, tool.Name, StringComparison.Ordinal)) words.AddRange(Words(tool.DisplayName));
        words.AddRange(Words(description));

        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in words) terms[word] = terms.GetValueOrDefault(word) + 1;

        return new Document(entry, terms, words.Count);
    }

    /// <summary>
    /// Lower-cased, split at anything that is not a letter or digit and at camelCase humps, stop
    /// words dropped, and each word cut back to a rough stem so "files" finds "file".
    /// </summary>
    public static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;

        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;

            var word = current.ToString().ToLower(CultureInfo.InvariantCulture);
            current.Clear();

            if (!StopWords.Contains(word)) words.Add(Stem(word));
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(c) && i > 0 && char.IsLower(text[i - 1])) Flush();
            current.Append(c);
        }

        Flush();
        return words;
    }

    /// <summary>
    /// A deliberately small stemmer: plurals and the commonest verb endings. Enough that "files",
    /// "listing" and "created" meet "file", "list" and "create"; anything cleverer would need a
    /// language, and tool descriptions arrive in whatever one their author wrote.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length <= 3) return word;

        var stem = word switch
        {
            _ when word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4 => word[..^3] + "y",
            _ when word.EndsWith("sses", StringComparison.Ordinal) => word[..^2],
            _ when word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5 => word[..^3],
            _ when word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4 => word[..^2],
            _ when word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
                                      && !word.EndsWith("us", StringComparison.Ordinal) => word[..^1],
            _ => word,
        };

        // "creating" and "created" lose their endings to "creat", and "create" has to meet them
        // there: a final e goes everywhere, rather than guessing where one was.
        return stem.Length > 4 && stem.EndsWith('e') ? stem[..^1] : stem;
    }

    private sealed record Document(ToolSearchEntry Entry, Dictionary<string, int> Terms, int Length);
}

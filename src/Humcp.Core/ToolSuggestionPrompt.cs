using System.Security.Cryptography;
using System.Text;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// The words the local model is given for tool search, and how its answer is read back (Phase 11).
/// The spike's (v2-llm-spike §6), which put the right tool in the top three for 34 of 35 requests.
/// </summary>
/// <remarks>
/// The prompt is in two halves on purpose. The catalogue is long — the corpus's 213 tools are about
/// 3,900 tokens — and is read once; each request is a few dozen tokens appended to it and rewound
/// afterwards. So the catalogue half must not depend on the request, and must not change unless the
/// tools did (<see cref="Fingerprint"/>).
/// </remarks>
public static class ToolSuggestionPrompt
{
    /// <summary>How many suggestions are asked for, and kept.</summary>
    public const int Picks = 3;

    /// <summary>A tool's line is cut here: the first sentence is what a model needs, and the catalogue is read whole.</summary>
    public const int SummaryLength = 200;

    /// <summary>
    /// The id a tool goes by in the prompt: server and name, as the spike used. Server first, so two
    /// servers' tools of the same name are two different lines.
    /// </summary>
    public static string Id(ToolSearchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{entry.ServerName}/{entry.Tool.Name}";
    }

    /// <summary>The catalogue half: an instruction and one line per tool. The user's turn is left open.</summary>
    public static string Catalogue(IReadOnlyList<ToolSearchEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var text = new StringBuilder()
            .Append("You help someone find the right tool in an app. Each line below is a tool id, a colon, and what the tool does.\n\n");

        foreach (var entry in catalogue)
            text.Append(Id(entry)).Append(": ").Append(TextSanitizer.Clean(entry.Tool.Summary, SummaryLength)).Append('\n');

        return text.ToString();
    }

    /// <summary>The request half, which closes the user's turn.</summary>
    public static string Question(string request)
        => $"\nThe request: \"{TextSanitizer.Clean(request, TextSanitizer.NameLength)}\"\n\n" +
           $"Answer with the {Picks} tool ids that best match, best first, one per line, and nothing else.";

    /// <summary>
    /// The tools the model named, in its order, at most <see cref="Picks"/>, each once. A line that is
    /// not exactly an id is still read if it contains exactly one — models number their answers and
    /// add backticks — and a line naming nothing in the catalogue is dropped, so the answer can only
    /// ever be tools that exist.
    /// </summary>
    public static IReadOnlyList<ToolSearchEntry> Parse(string answer, IReadOnlyList<ToolSearchEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (string.IsNullOrWhiteSpace(answer)) return [];

        var byId = new Dictionary<string, ToolSearchEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalogue) byId.TryAdd(Id(entry), entry);

        var picked = new List<ToolSearchEntry>();

        foreach (var raw in answer.Split('\n'))
        {
            // "1. ", "2) " — but only as a list number, so a server called "1password" keeps its name.
            var line = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"^\d+[.)]\s*", "");
            line = line.Trim('`', '*', '-', ' ', '"', '\'').TrimEnd('.');

            var entry = byId.TryGetValue(line, out var exact)
                ? exact
                : byId.Where(p => line.Contains(p.Key, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(p => p.Key.Length)
                      .Select(p => p.Value)
                      .FirstOrDefault();

            if (entry is not null && !picked.Contains(entry)) picked.Add(entry);
            if (picked.Count == Picks) break;
        }

        return picked;
    }

    /// <summary>
    /// What the catalogue half was built from, so a suggester can tell whether what it read is still
    /// the catalogue. Every line counts: a changed description is a changed prompt.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<ToolSearchEntry> catalogue)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Catalogue(catalogue))));
}

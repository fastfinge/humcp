using System.Text;

namespace Humcp.Schema;

/// <summary>
/// Turns an identifier into something worth hearing. ROADMAP §7.1: a control's accessible name
/// comes from <c>title ?? name</c>, humanised — <c>snake_case</c> and <c>camelCase</c> become
/// words, because a screen reader reads "list_pull_requests" as one long stumble.
/// </summary>
public static class NameHumanizer
{
    /// <summary>
    /// <c>list_pull_requests</c> → "List pull requests"; <c>getTinyImage</c> → "Get tiny image";
    /// <c>read_URL</c> → "Read URL". Acronym runs are preserved, since expanding them is guessing.
    /// </summary>
    public static string Humanize(string? identifier)
    {
        var text = TextSanitizer.Clean(identifier, TextSanitizer.NameLength);
        if (text.Length == 0) return "";

        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            words.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '_' or '-' or '.' or '/' or ':' or ' ')
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && IsBoundary(text, i)) Flush();

            current.Append(c);
        }

        Flush();
        if (words.Count == 0) return text;

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var isAcronym = word.Length > 1 && word.All(char.IsUpper);

            if (isAcronym) continue;

            words[i] = i == 0
                ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                : word.ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// What to show and speak for something that has both a machine name and an optional
    /// human-readable title. The title wins when present; otherwise the name is humanised.
    /// </summary>
    public static string DisplayName(string? title, string? name)
    {
        var fromTitle = TextSanitizer.Clean(title, TextSanitizer.NameLength);
        if (fromTitle.Length > 0) return fromTitle;

        var humanised = Humanize(name);
        return humanised.Length > 0 ? humanised : TextSanitizer.Clean(name, TextSanitizer.NameLength);
    }

    /// <summary>
    /// A camel-case boundary: a lower-to-upper transition, an acronym running into a word
    /// (<c>URLPath</c> → "URL Path"), or a letter running into a digit.
    /// </summary>
    private static bool IsBoundary(string text, int i)
    {
        var previous = text[i - 1];
        var c = text[i];

        if (char.IsLower(previous) && char.IsUpper(c)) return true;
        if (char.IsUpper(previous) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1])) return true;
        if (char.IsLetter(previous) && char.IsDigit(c)) return true;
        if (char.IsDigit(previous) && char.IsLetter(c)) return true;

        return false;
    }
}

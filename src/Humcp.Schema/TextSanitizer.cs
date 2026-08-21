using System.Globalization;
using System.Text;

namespace Humcp.Schema;

/// <summary>
/// Hygiene for third-party text. Every string a server sends — titles, descriptions, enum values,
/// result text — is untrusted (ROADMAP §4, pitfall 5) and is heading either for UI Automation or
/// for a speech synthesiser. Both deserve the same treatment: no control characters, no runaway
/// whitespace, no unbounded length.
/// </summary>
/// <remarks>
/// Pure text, no schema vocabulary, no dependencies — it lives here because Humcp.Schema is the
/// only project both Humcp.Core and Humcp.Speech are allowed to point at.
/// </remarks>
public static class TextSanitizer
{
    /// <summary>Cap for a control's accessible name. Long names are read in full, every time.</summary>
    public const int NameLength = 200;

    /// <summary>Cap for help text. Tool descriptions are written for models and run to pages.</summary>
    public const int HelpLength = 400;

    /// <summary>Cap for a single spoken utterance.</summary>
    public const int SpeechLength = 1000;

    /// <summary>Cap for a full description shown on demand in a details window.</summary>
    public const int DetailsLength = 20_000;

    /// <summary>
    /// Collapses a string to a single safe line: control and formatting characters become spaces,
    /// runs of whitespace collapse to one, and the result is trimmed and capped at
    /// <paramref name="maxLength"/> including the ellipsis.
    /// </summary>
    public static string Clean(string? text, int maxLength = HelpLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 16));
        var pendingSpace = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsDiscarded(rune)) continue;

            if (IsWhitespaceLike(rune))
            {
                // Never leads; only emitted once something follows it.
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(rune);

            // Keep going a little past the cap so Truncate can still find a word boundary.
            if (sb.Length > maxLength + 32) break;
        }

        return Truncate(sb.ToString(), maxLength);
    }

    /// <summary>
    /// Multi-line variant, for text a user will read with a caret rather than hear in one breath.
    /// Line structure <em>and indentation</em> survive; control characters, formatting characters
    /// and trailing whitespace do not.
    /// </summary>
    /// <remarks>
    /// Deliberately does not collapse runs of spaces the way <see cref="Clean"/> does. Results
    /// arrive as JSON, tables and aligned columns, and the read-only result box exists so they can
    /// be reviewed line by line (ROADMAP §7.9) — collapsing the indentation would throw away the
    /// only structure such a result has. Tabs become a single space, so that "no control
    /// characters" still holds for anything a synthesiser might meet.
    /// </remarks>
    public static string CleanMultiline(string? text, int maxLength = DetailsLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 16));

        foreach (var rune in text.ReplaceLineEndings("\n").EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                TrimTrailingSpaces(sb);
                sb.Append('\n');
            }
            else if (IsDiscarded(rune))
            {
                continue;
            }
            else if (rune.Value == '\t')
            {
                sb.Append(' ');
            }
            else if (Rune.IsControl(rune))
            {
                continue;
            }
            else
            {
                sb.Append(rune);
            }

            if (sb.Length > maxLength + 32) break;
        }

        TrimTrailingSpaces(sb);
        var joined = sb.ToString().TrimEnd('\n').ReplaceLineEndings();

        return Truncate(joined, maxLength);
    }

    /// <summary>
    /// The first sentence, for <c>AutomationProperties.HelpText</c> (ROADMAP §4, pitfall 1). Stops
    /// at the first sentence-ending punctuation or the first line break, whichever comes first, so
    /// a description whose first line is a heading does not drag in the paragraph below it.
    /// </summary>
    public static string FirstSentence(string? text, int maxLength = HelpLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var upToBreak = text.ReplaceLineEndings("\n").Split('\n', 2)[0];
        if (string.IsNullOrWhiteSpace(upToBreak))
        {
            // A description that opens with a blank line: fall back to the whole thing.
            upToBreak = text;
        }

        var cleaned = Clean(upToBreak, maxLength);
        if (cleaned.Length == 0) return "";

        for (var i = 0; i < cleaned.Length; i++)
        {
            if (cleaned[i] is not ('.' or '!' or '?')) continue;

            // A terminator ends a sentence when whitespace follows it, or when nothing does.
            // "v1.2" fails the first test; "e.g." fails the abbreviation test. What follows is
            // deliberately not consulted — real descriptions are not reliably capitalised, and
            // requiring a capital lets a ten-thousand-character paragraph through whole.
            if (i + 1 == cleaned.Length) return cleaned;
            if (cleaned[i + 1] != ' ') continue;
            if (i >= 1 && IsAbbreviationEnd(cleaned, i)) continue;

            return cleaned[..(i + 1)];
        }

        return cleaned;
    }

    /// <summary>
    /// Collapses a string for a speech synthesiser. Identical to <see cref="Clean"/> with a cap
    /// suited to one utterance; named separately because the call sites read better and because a
    /// future braille policy (ROADMAP §13) may want to diverge here.
    /// </summary>
    public static string CleanForSpeech(string? text) => Clean(text, SpeechLength);

    private static bool IsAbbreviationEnd(string text, int terminator)
    {
        // Single letter before the dot ("e.g.", "U.S.") — treat as an abbreviation, not an end.
        var previousLetters = 0;
        for (var i = terminator - 1; i >= 0 && char.IsLetter(text[i]); i--) previousLetters++;
        return previousLetters == 1;
    }

    private static bool IsDiscarded(Rune rune)
    {
        // Bidi overrides, zero-width joiners and friends: invisible on screen, and a well-known
        // way to make one string read as another.
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Format
                        or UnicodeCategory.PrivateUse
                        or UnicodeCategory.Surrogate
                        or UnicodeCategory.OtherNotAssigned;
    }

    private static bool IsWhitespaceLike(Rune rune)
        => Rune.IsWhiteSpace(rune) || Rune.IsControl(rune);

    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        if (maxLength == 1) return "…";

        var cut = text[..(maxLength - 1)];
        var lastSpace = cut.LastIndexOf(' ');

        // Only honour the word boundary if it does not throw most of the text away.
        if (lastSpace > maxLength / 2) cut = cut[..lastSpace];

        return cut.TrimEnd() + "…";
    }
}

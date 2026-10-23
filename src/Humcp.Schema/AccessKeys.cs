namespace Humcp.Schema;

/// <summary>
/// Picks one character of each label to be its keyboard shortcut, without two labels claiming the
/// same one (ROADMAP §7.3: a real label with an access key, on every field).
/// </summary>
/// <remarks>
/// <para>
/// Answers with an index into the label rather than with marked-up text. Which character is the
/// shortcut is a fact about the label; spelling that as a leading underscore is WPF's convention,
/// and this assembly does not know WPF exists (§6.1, hedge 1).
/// </para>
/// <para>
/// A duplicate is worse than an absence here. Windows cycles focus between controls sharing a key,
/// which on a generated form of twenty-three fields means <kbd>Alt</kbd>+<kbd>S</kbd> visits four
/// unrelated boxes in an order nobody can predict. Where the letters run out the answer is null,
/// and the field is reached by <kbd>Tab</kbd> like everything else.
/// </para>
/// </remarks>
public static class AccessKeys
{
    /// <summary>
    /// Letters a generated form's own chrome takes before any field does: the <b>T</b>ool and
    /// <b>W</b>indow menus, and the <b>R</b>un button, <b>C</b>lose button and
    /// <b>o</b>ptional-parameters toggle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two menus are on this list because a manual pass found them missing from it: a form with
    /// a Tags parameter gave that field <kbd>Alt</kbd>+<kbd>T</kbd>, and pressing it opened the Tool
    /// menu instead. A menu bar mnemonic wins over a control's, so the field was simply unreachable
    /// by its own access key (phase3-notes 4.6). The menu bar was added after this list was written
    /// and nothing connected the two.
    /// </para>
    /// <para>
    /// The buttons <em>inside</em> a field — Add, Remove, Edit — deliberately have no access key at
    /// all. Their letters would be fixed while the labels around them are whatever a server called
    /// its parameters, and a form with two lists on it would have two Add buttons claiming the same
    /// letter anyway. They are one <kbd>Tab</kbd> from the box they belong to.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<char> WindowCommands = ['t', 'w', 'r', 'c', 'o'];

    /// <summary>
    /// The same list for the window a server's own question opens: the <b>Q</b>uestion and
    /// <b>W</b>indow menus, and the <b>A</b>ccept, <b>D</b>ecline and <b>C</b>ancel buttons.
    /// </summary>
    /// <remarks>
    /// A separate list rather than a longer shared one. Reserving a letter costs every form that
    /// never had that button, and a question window's three answers are fixed while a tool form's
    /// commands are different fixed ones.
    /// </remarks>
    public static readonly IReadOnlyList<char> QuestionCommands = ['q', 'w', 'a', 'd', 'c'];

    /// <summary>
    /// The same list for a prompt's form (Phase 12): the <b>P</b>rompt and <b>W</b>indow menus, and
    /// <b>G</b>et prompt, <b>C</b>lose and show <b>o</b>ptional. Found by ear: the prompt form kept
    /// the tool form's list, so an argument called "Goal" would have shared G with the button.
    /// </summary>
    public static readonly IReadOnlyList<char> PromptCommands = ['p', 'w', 'g', 'c', 'o'];

    /// <summary>
    /// The same list for a resource's window (Phase 13): the Re<b>s</b>ource and <b>W</b>indow menus,
    /// and <b>R</b>ead resource, <b>C</b>lose and show <b>o</b>ptional.
    /// </summary>
    public static readonly IReadOnlyList<char> ResourceCommands = ['s', 'w', 'r', 'c', 'o'];

    /// <summary>
    /// One index per label, in the same order, or null where nothing was left to give it.
    /// </summary>
    public static IReadOnlyList<int?> Assign(IReadOnlyList<string> labels, IEnumerable<char>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var taken = new HashSet<char>(reserved?.Select(char.ToLowerInvariant) ?? []);
        var assigned = new int?[labels.Count];

        for (var i = 0; i < labels.Count; i++) assigned[i] = Claim(labels[i], taken);

        return assigned;
    }

    private static int? Claim(string label, HashSet<char> taken)
    {
        foreach (var index in Candidates(label))
        {
            var key = char.ToLowerInvariant(label[index]);
            if (!taken.Add(key)) continue;

            return index;
        }

        return null;
    }

    /// <summary>
    /// Where to look, in order of how recognisable the result is: the first letter of the label,
    /// then the first letter of each later word, then anything else. "Include archived" offering
    /// <kbd>Alt</kbd>+<kbd>A</kbd> is a shortcut somebody could guess; offering
    /// <kbd>Alt</kbd>+<kbd>C</kbd> is one they would have to be told.
    /// </summary>
    private static IEnumerable<int> Candidates(string label)
    {
        var starts = new List<int>();
        var rest = new List<int>();
        var atWordStart = true;

        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];

            if (!char.IsLetterOrDigit(c))
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart) starts.Add(i);
            else rest.Add(i);

            atWordStart = false;
        }

        return starts.Concat(rest);
    }
}

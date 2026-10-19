using System.Globalization;

namespace Humcp.Schema;

/// <summary>
/// The order a result table's rows go in when it is sorted by one of its columns (ROADMAP §10,
/// Phase 6), and the sentence that says so.
/// </summary>
/// <remarks>
/// <para>
/// A cell is text by the time it reaches a table, so text is what is compared — but two numbers
/// compare as numbers, because "10" before "9" is a sort nobody asked for, and a screen reader
/// user arrowing down a column has no way to see that the order is wrong at a glance.
/// </para>
/// <para>
/// Empty cells go last in either direction: a column sorted to find the largest value should not
/// start with every row that has none.
/// </para>
/// </remarks>
public sealed class ResultSort(int column, bool ascending) : IComparer<ResultRow>, System.Collections.IComparer
{
    public int Column { get; } = column;

    public bool Ascending { get; } = ascending;

    public int Compare(ResultRow? x, ResultRow? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var a = Text(x);
        var b = Text(y);

        if (a.Length == 0 || b.Length == 0)
        {
            // Not reversed by the direction: empty is last both ways. Row number breaks the tie so
            // the sort is stable, and a reader's "row 3" means the same row on every pass.
            if (a.Length != b.Length) return a.Length == 0 ? 1 : -1;
            return x.Number.CompareTo(y.Number);
        }

        var order = Values(a, b);
        if (order == 0) return x.Number.CompareTo(y.Number);

        return Ascending ? order : -order;
    }

    int System.Collections.IComparer.Compare(object? x, object? y) => Compare(x as ResultRow, y as ResultRow);

    /// <summary>"Sorted by Name, ascending." What a header click or a menu command says it did.</summary>
    public static string Describe(string columnLabel, bool ascending)
        => $"Sorted by {TextSanitizer.Clean(columnLabel, TextSanitizer.NameLength)}, {(ascending ? "ascending" : "descending")}.";

    /// <summary>
    /// The header a sorted column shows, which is also what a reader says on entering that column —
    /// so the sort is a fact that is there every time, not an announcement that was made once
    /// (§7.24).
    /// </summary>
    public static string Header(string columnLabel, bool? ascending) => ascending switch
    {
        true => $"{columnLabel}, sorted ascending",
        false => $"{columnLabel}, sorted descending",
        null => columnLabel,
    };

    private string Text(ResultRow row) => Column >= 0 && Column < row.Cells.Count ? row.Cells[Column].Text.Trim() : "";

    private static int Values(string a, string b)
    {
        if (Number(a) is { } left && Number(b) is { } right) return left.CompareTo(right);

        // A number against a word: numbers first, as every file manager on Windows does.
        if (Number(a) is not null) return -1;
        if (Number(b) is not null) return 1;

        return string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
    }

    private static double? Number(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
}
